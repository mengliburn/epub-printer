using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using EpubPrinter.Core;
using Xunit;

namespace EpubPrinter.Tests;

/// <summary>
/// Exercises the html -> FlowDocument -> paginated pages pipeline. These need an STA
/// thread because they build WPF document objects, hence <see cref="StaFactAttribute"/>.
/// </summary>
public sealed class DocumentPipelineTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "epubprinter-tests", Guid.NewGuid().ToString("N"));

    private string PathFor(string name) => Path.Combine(_folder, name);

    [StaFact]
    public void Converts_common_html_constructs_into_blocks()
    {
        const string html = "<html><body>" +
                            "<h1>Heading</h1>" +
                            "<p>Plain <b>bold</b> and <i>italic</i> and an &amp; entity.</p>" +
                            "<ul><li>One</li><li>Two</li></ul>" +
                            "<ol><li>First</li></ol>" +
                            "<blockquote><p>Quoted</p></blockquote>" +
                            "<table><tr><th>H</th></tr><tr><td>C</td></tr></table>" +
                            "<pre>code</pre><hr/>" +
                            "<script>ignored()</script>" +
                            "</body></html>";

        var blocks = new HtmlToFlowConverter(null, "c.xhtml", PrintOptions.Default).Convert(html);

        Assert.Contains(blocks, b => b is Paragraph p && GetText(p).Contains("Heading"));
        Assert.Contains(blocks, b => b is List list && list.MarkerStyle == TextMarkerStyle.Disc);
        Assert.Contains(blocks, b => b is List list && list.MarkerStyle == TextMarkerStyle.Decimal);
        Assert.Contains(blocks, b => b is Section);
        Assert.Contains(blocks, b => b is Table);
        Assert.Contains(blocks, b => b is BlockUIContainer);

        var text = string.Concat(blocks.OfType<Paragraph>().Select(GetText));
        Assert.Contains("Plain bold and italic and an & entity.", text);
        Assert.DoesNotContain("ignored()", text);
    }

    [StaFact]
    public void Whitespace_is_collapsed_like_a_browser()
    {
        Assert.Equal(" a b c ", HtmlToFlowConverter.NormalizeWhitespace("  a \n\t b   c "));

        var blocks = new HtmlToFlowConverter(null, "c.xhtml", PrintOptions.Default)
            .Convert("<html><body><p>  spaced\n   out   words  </p></body></html>");

        Assert.Equal("spaced out words", GetText(Assert.IsType<Paragraph>(blocks[0])));
    }

    [StaFact]
    public void Text_alignment_from_markup_is_honoured()
    {
        var blocks = new HtmlToFlowConverter(null, "c.xhtml", PrintOptions.Default)
            .Convert("<html><body><p style='text-align:center'>middle</p><p align='right'>right</p></body></html>");

        Assert.Equal(TextAlignment.Center, ((Paragraph)blocks[0]).TextAlignment);
        Assert.Equal(TextAlignment.Right, ((Paragraph)blocks[1]).TextAlignment);
    }

    [StaFact]
    public void Images_are_embedded_when_enabled_and_skipped_when_not()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("img.epub"), chapterCount: 3));
        var chapter = book.Chapters[1];
        var html = book.ReadChapterText(chapter);

        var withImages = new HtmlToFlowConverter(book, chapter.Href, new PrintOptions { IncludeImages = true }).Convert(html);
        Assert.Contains(withImages.OfType<Paragraph>().SelectMany(p => p.Inlines), i => i is InlineUIContainer);

        var withoutImages = new HtmlToFlowConverter(book, chapter.Href, new PrintOptions { IncludeImages = false }).Convert(html);
        Assert.DoesNotContain(withoutImages.OfType<Paragraph>().SelectMany(p => p.Inlines), i => i is InlineUIContainer);
    }

    [StaFact]
    public void Builds_a_document_for_a_chapter_range_with_page_breaks()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("range.epub"), chapterCount: 6));
        var selection = DocumentBuilder.SelectRange(book, 2, 4);
        var options = new PrintOptions { StartChapterOnNewPage = true, IncludeChapterTitles = true };

        var document = DocumentBuilder.Build(book, selection, options);
        var text = new TextRange(document.ContentStart, document.ContentEnd).Text;

        Assert.Contains("Chapter 2", text);
        Assert.Contains("Chapter 4", text);
        Assert.DoesNotContain("Chapter 1:", text);
        Assert.DoesNotContain("Chapter 5:", text);

        // Two of the three chapters start a new page; the first must not.
        Assert.Equal(2, document.Blocks.Count(b => b.BreakPageBefore));
        Assert.False(document.Blocks.FirstBlock!.BreakPageBefore);
    }

    [StaFact]
    public void Page_breaks_are_omitted_when_the_option_is_off()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("nobreak.epub"), chapterCount: 3));

        var document = DocumentBuilder.Build(book, book.Chapters, new PrintOptions { StartChapterOnNewPage = false });

        Assert.DoesNotContain(document.Blocks, b => b.BreakPageBefore);
    }

    [StaFact]
    public void Whole_book_paginates_into_multiple_pages()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("all.epub"), chapterCount: 5));
        var options = new PrintOptions { PaperSize = PaperSize.A5, FontSize = 12 };

        var document = DocumentBuilder.Build(book, book.Chapters, options);
        var pages = PrintService.CountPages(document, options).Pages;

        Assert.True(pages >= 5, $"expected at least one page per chapter but got {pages}");
    }

    [StaFact]
    public void Exports_the_selected_range_to_a_readable_xps_package()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("xps.epub"), chapterCount: 5));
        var options = new PrintOptions { PaperSize = PaperSize.Letter, ShowPageNumbers = true, ShowRunningHeader = true, PagesPerSheet = 1 };
        var selection = DocumentBuilder.SelectRange(book, 2, 3);

        var document = DocumentBuilder.Build(book, selection, options);
        var target = PathFor("range.xps");
        var pageCount = PrintService.ExportToXps(document, "The Sample Book - Chapters 2-3", options, target).Sheets;

        Assert.True(File.Exists(target));
        Assert.True(new FileInfo(target).Length > 0);
        Assert.True(pageCount >= 2, $"expected at least 2 pages, got {pageCount}");

        using var package = new System.Windows.Xps.Packaging.XpsDocument(target, FileAccess.Read);
        var sequence = package.GetFixedDocumentSequence();
        Assert.NotNull(sequence);
        Assert.Equal(pageCount, sequence!.DocumentPaginator.PageCount);
        Assert.NotNull(sequence.DocumentPaginator.GetPage(0).Visual);

        var text = new TextRange(document.ContentStart, document.ContentEnd).Text;
        Assert.Contains("Chapter 2", text);
        Assert.DoesNotContain("Chapter 4", text);
    }

    [StaFact]
    public void Header_and_page_numbers_are_stamped_onto_every_page()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("header.epub"), chapterCount: 2));
        var options = new PrintOptions { ShowRunningHeader = true, ShowPageNumbers = true, PaperSize = PaperSize.A5, PagesPerSheet = 1 };
        var document = DocumentBuilder.Build(book, book.Chapters, options);

        var paginator = (HeaderFooterPaginator)PrintService.CreatePaginator(document, "Running Header", options, options.PageSizeDiu);

        // Asking for the first page must be enough to know the total, so the footer
        // can render "page x of y" even before the caller paginates everything.
        var firstPage = paginator.GetPage(0);
        Assert.NotNull(firstPage.Visual);
        Assert.True(paginator.IsPageCountValid);
        Assert.True(paginator.PageCount > 0);

        for (var i = 0; i < paginator.PageCount; i++)
        {
            var page = paginator.GetPage(i);
            Assert.NotNull(page.Visual);
            Assert.Equal(options.PageSizeDiu.Width, page.Size.Width, 3);
        }

        // Pages are frequently requested twice (preview navigation, reprints).
        Assert.NotNull(paginator.GetPage(0).Visual);
        Assert.NotNull(paginator.GetPage(0).Visual);
    }

    [StaFact]
    public void Scaling_down_fits_more_text_on_each_sheet()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("scale.epub"), chapterCount: 4));

        var full = new PrintOptions { ScalePercent = 100, StartChapterOnNewPage = false };
        var half = new PrintOptions { ScalePercent = 50, StartChapterOnNewPage = false };

        var fullPages = PrintService.CountPages(DocumentBuilder.Build(book, book.Chapters, full), full).Pages;
        var halfPages = PrintService.CountPages(DocumentBuilder.Build(book, book.Chapters, half), half).Pages;

        Assert.True(halfPages < fullPages, $"50% should need fewer sheets ({halfPages} vs {fullPages})");
    }

    [StaFact]
    public void Scaled_pages_keep_the_physical_sheet_size()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("scalesize.epub"), chapterCount: 2));
        var options = new PrintOptions { ScalePercent = 60, PaperSize = PaperSize.A4, PagesPerSheet = 1 };
        var document = DocumentBuilder.Build(book, book.Chapters, options);

        var paginator = (HeaderFooterPaginator)PrintService.CreatePaginator(document, "Header", options, options.PageSizeDiu);
        var page = paginator.GetPage(0);

        Assert.Equal(options.PageSizeDiu.Width, page.Size.Width, 3);
        Assert.Equal(options.PageSizeDiu.Height, page.Size.Height, 3);

        // The content itself is laid out on a correspondingly larger logical page.
        Assert.Equal(options.PageSizeDiu.Width / 0.6, document.PageWidth, 3);
    }

    [StaFact]
    public void Landscape_swaps_the_page_dimensions()
    {
        var portrait = new PrintOptions { PaperSize = PaperSize.Letter, Landscape = false };
        var landscape = new PrintOptions { PaperSize = PaperSize.Letter, Landscape = true };

        Assert.Equal(portrait.PageSizeDiu.Width, landscape.PageSizeDiu.Height, 3);
        Assert.Equal(portrait.PageSizeDiu.Height, landscape.PageSizeDiu.Width, 3);

        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("land.epub"), chapterCount: 2));
        var document = DocumentBuilder.Build(book, book.Chapters, landscape);
        var paginator = PrintService.CreatePaginator(document, string.Empty, landscape, landscape.PageSizeDiu);

        Assert.True(paginator.GetPage(0).Size.Width > paginator.GetPage(0).Size.Height);
    }

    [StaFact]
    public void Exported_xps_honours_the_print_scale()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("scalexps.epub"), chapterCount: 3));
        var options = new PrintOptions { ScalePercent = 75 };
        var document = DocumentBuilder.Build(book, book.Chapters, options);
        var target = PathFor("scaled.xps");

        var pages = PrintService.ExportToXps(document, "Scaled", options, target).Pages;

        Assert.True(pages > 0);
        using var package = new System.Windows.Xps.Packaging.XpsDocument(target, FileAccess.Read);
        var sequence = package.GetFixedDocumentSequence()!;
        var page = sequence.DocumentPaginator.GetPage(0);
        Assert.Equal(options.PageSizeDiu.Width, page.Size.Width, 1);
    }

    [StaFact]
    public void Two_pages_per_side_halves_the_number_of_sheets()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("nup.epub"), chapterCount: 5));

        var single = new PrintOptions { PagesPerSheet = 1 };
        var double2 = new PrintOptions { PagesPerSheet = 2 };

        var one = PrintService.CountPages(DocumentBuilder.Build(book, book.Chapters, single), single);
        var two = PrintService.CountPages(DocumentBuilder.Build(book, book.Chapters, double2), double2);

        // The document itself is unchanged; only the packing onto sheets differs.
        Assert.Equal(one.Pages, two.Pages);
        Assert.Equal(one.Pages, one.Sheets);
        Assert.Equal((int)Math.Ceiling(one.Pages / 2.0), two.Sheets);
    }

    [StaFact]
    public void Four_pages_per_side_quarters_the_number_of_sheets()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("nup4.epub"), chapterCount: 8));
        var options = new PrintOptions { PagesPerSheet = 4 };

        var counts = PrintService.CountPages(DocumentBuilder.Build(book, book.Chapters, options), options);

        Assert.Equal((int)Math.Ceiling(counts.Pages / 4.0), counts.Sheets);
    }

    [StaFact]
    public void Packed_sheets_keep_the_paper_size_and_hold_every_page()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("nupsize.epub"), chapterCount: 4));
        var options = new PrintOptions { PagesPerSheet = 2, PaperSize = PaperSize.Letter };
        var document = DocumentBuilder.Build(book, book.Chapters, options);

        var paginator = (NUpPaginator)PrintService.CreatePaginator(document, "Header", options, options.PageSizeDiu);
        paginator.Inner.ComputePageCount();

        var page = paginator.GetPage(0);
        Assert.Equal(options.PageSizeDiu.Width, page.Size.Width, 3);
        Assert.Equal(options.PageSizeDiu.Height, page.Size.Height, 3);

        // Two pages were placed on the first sheet.
        var sheet = Assert.IsType<ContainerVisual>(VisualTreeHelper.GetChild(page.Visual, 0));
        Assert.Equal(2, sheet.Children.Count);

        // The last sheet may be partly empty but must never be missing.
        Assert.NotEqual(DocumentPage.Missing, paginator.GetPage(paginator.PageCount - 1));
        Assert.Equal(DocumentPage.Missing, paginator.GetPage(paginator.PageCount));
    }

    [StaFact]
    public void Duplex_choice_reaches_the_print_ticket()
    {
        var queue = PrintService.GetPrintQueues().FirstOrDefault();

        var oneSided = PrintService.BuildTicket(queue, new PrintOptions { Duplex = DuplexMode.OneSided });
        var longEdge = PrintService.BuildTicket(queue, new PrintOptions { Duplex = DuplexMode.LongEdge });
        var shortEdge = PrintService.BuildTicket(queue, new PrintOptions { Duplex = DuplexMode.ShortEdge }, copies: 3);

        Assert.Equal(Duplexing.OneSided, oneSided.Duplexing);
        Assert.Equal(Duplexing.TwoSidedLongEdge, longEdge.Duplexing);
        Assert.Equal(Duplexing.TwoSidedShortEdge, shortEdge.Duplexing);
        Assert.Equal(3, shortEdge.CopyCount);
    }

    [StaFact]
    public void Sheets_of_paper_account_for_double_sided_printing()
    {
        var counts = new PrintService.PageCounts(Pages: 10, Sheets: 5);

        Assert.Equal(5, counts.SheetsOfPaper(DuplexMode.OneSided));
        Assert.Equal(3, counts.SheetsOfPaper(DuplexMode.LongEdge));
        Assert.Equal(3, counts.SheetsOfPaper(DuplexMode.ShortEdge));
    }

    [StaFact]
    public void Exported_xps_contains_the_packed_sheets()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("nupxps.epub"), chapterCount: 4));
        var options = new PrintOptions { PagesPerSheet = 2 };
        var document = DocumentBuilder.Build(book, book.Chapters, options);
        var target = PathFor("packed.xps");

        var counts = PrintService.ExportToXps(document, "Packed", options, target);

        using var package = new System.Windows.Xps.Packaging.XpsDocument(target, FileAccess.Read);
        var sequence = package.GetFixedDocumentSequence()!;

        Assert.Equal(counts.Sheets, sequence.DocumentPaginator.PageCount);
        Assert.True(counts.Sheets < counts.Pages);
        Assert.Equal(options.PageSizeDiu.Width, sequence.DocumentPaginator.GetPage(0).Size.Width, 1);
    }

    [StaFact]
    public void Broken_chapter_markup_does_not_break_the_build()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("ok.epub"), chapterCount: 2));

        var blocks = new HtmlToFlowConverter(book, book.Chapters[0].Href, PrintOptions.Default)
            .Convert("<html><body><p>unclosed <b>bold<p>next</body>");

        Assert.NotEmpty(blocks);
    }

    [StaFact]
    public void Chapter_title_is_not_printed_twice()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("dupe.epub"), chapterCount: 2));
        var options = new PrintOptions { IncludeChapterTitles = true, StartChapterOnNewPage = false };

        var document = DocumentBuilder.Build(book, DocumentBuilder.SelectRange(book, 1, 1), options);
        var text = new TextRange(document.ContentStart, document.ContentEnd).Text;

        var occurrences = CountOccurrences(text, "Chapter 1: The Number 1");
        Assert.Equal(1, occurrences);
    }

    [StaFact]
    public void Chapter_title_is_added_when_the_markup_has_no_heading()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateWithoutToc(PathFor("noheading.epub")));
        var options = new PrintOptions { IncludeChapterTitles = true, StartChapterOnNewPage = false };

        // The second chapter has no heading, so the title comes from the builder.
        var document = DocumentBuilder.Build(book, DocumentBuilder.SelectRange(book, 2, 2), options);
        var text = new TextRange(document.ContentStart, document.ContentEnd).Text;

        Assert.Contains("Title Element", text);
        Assert.Contains("No heading here.", text);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private static string GetText(Paragraph paragraph) =>
        new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }
}



