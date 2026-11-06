using System.IO;
using EpubPrinter.App;
using EpubPrinter.Core;
using Xunit;

namespace EpubPrinter.Tests;

public sealed class FullSheetScannerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "epubprinter-tests", Guid.NewGuid().ToString("N"));

    private EpubBook OpenBook(int chapterCount) =>
        EpubReader.Open(SampleEpubFactory.CreateCustom(
            Path.Combine(_folder, "scan.epub"), chapterCount, paragraphsPerChapter: 0));

    [Theory]
    [InlineData(0, 2, DuplexMode.ShortEdge, 0, 0)]
    [InlineData(3, 1, DuplexMode.OneSided, 0, 0)]
    [InlineData(3, 1, DuplexMode.LongEdge, 0, 1)]
    [InlineData(5, 2, DuplexMode.OneSided, 1, 0)]
    [InlineData(5, 2, DuplexMode.ShortEdge, 1, 1)]
    [InlineData(8, 2, DuplexMode.ShortEdge, 0, 0)]
    [InlineData(9, 4, DuplexMode.OneSided, 3, 0)]
    [InlineData(9, 6, DuplexMode.LongEdge, 3, 0)]
    [InlineData(1, 9, DuplexMode.LongEdge, 8, 1)]
    public void Blank_slots_include_partial_sides_and_empty_duplex_backs(
        int pages, int perSide, DuplexMode duplex, int expectedOnSide, int expectedBlankSides)
    {
        var sides = (pages + perSide - 1) / perSide;
        var blanks = new PrintService.PageCounts(pages, sides).GetBlankSlots(perSide, duplex);

        Assert.Equal(expectedOnSide, blanks.OnLastSide);
        Assert.Equal(expectedBlankSides, blanks.BlankSides);
        Assert.Equal(expectedOnSide + (expectedBlankSides * perSide), blanks.Total);
        Assert.Equal(blanks.Total > 0, blanks.Any);
    }

    [StaFact]
    public void Finds_the_first_full_sheet_range_from_the_selected_start()
    {
        using var book = OpenBook(12);
        var options = new PrintOptions { PagesPerSheet = 2, Duplex = DuplexMode.ShortEdge };

        var outcome = FullSheetScanner.Scan(book, startChapter: 3, options);

        Assert.Equal(3, outcome.StartChapter);
        Assert.Equal(4, outcome.Examined);
        var full = Assert.IsType<FullSheetScanner.Candidate>(outcome.Full);
        Assert.Equal(6, full.LastChapter);
        Assert.Equal(4, full.Pages);
        Assert.Equal(2, full.Sides);
        Assert.False(full.Blanks.Any);

        for (var end = 3; end < full.LastChapter; end++)
        {
            var chapters = DocumentBuilder.SelectRange(book, 3, end);
            var counts = PrintService.CountPages(DocumentBuilder.Build(book, chapters, options), options);
            Assert.True(counts.GetBlankSlots(options.PagesPerSheet, options.Duplex).Any);
        }

        var selected = DocumentBuilder.SelectRange(book, 3, full.LastChapter);
        var selectedCounts = PrintService.CountPages(DocumentBuilder.Build(book, selected, options), options);
        Assert.Equal(full.Pages, selectedCounts.Pages);
        Assert.Equal(full.Sides, selectedCounts.Sheets);
    }

    [StaFact]
    public void Reports_the_closest_range_when_no_match_occurs_within_ten_chapters()
    {
        using var book = OpenBook(25);
        var options = new PrintOptions { PagesPerSheet = 9, Duplex = DuplexMode.LongEdge };

        var outcome = FullSheetScanner.Scan(book, startChapter: 5, options);

        Assert.Null(outcome.Full);
        Assert.Equal(10, outcome.Examined);
        var closest = Assert.IsType<FullSheetScanner.Candidate>(outcome.Closest);
        Assert.Equal(14, closest.LastChapter);
        Assert.Equal(10, closest.Pages);
        Assert.Equal(8, closest.Blanks.Total);
    }

    [StaFact]
    public void Stops_at_the_end_of_the_book_and_honors_one_sided_printing()
    {
        using var book = OpenBook(5);
        var options = new PrintOptions { PagesPerSheet = 2, Duplex = DuplexMode.OneSided };

        var full = FullSheetScanner.Scan(book, startChapter: 4, options);
        Assert.Equal(5, Assert.IsType<FullSheetScanner.Candidate>(full.Full).LastChapter);
        Assert.Equal(2, full.Examined);

        var partial = FullSheetScanner.Scan(book, startChapter: 5, options);
        Assert.Null(partial.Full);
        Assert.Equal(1, partial.Examined);
        Assert.Equal(1, Assert.IsType<FullSheetScanner.Candidate>(partial.Closest).Blanks.Total);
    }

    [StaFact]
    public void Uses_actual_pagination_when_chapters_flow_together()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateCustom(
            Path.Combine(_folder, "flow.epub"), 12, paragraphsPerChapter: 3));
        var options = new PrintOptions
        {
            PagesPerSheet = 2,
            Duplex = DuplexMode.OneSided,
            StartChapterOnNewPage = false,
            IncludeTitlePage = true,
            ScalePercent = 75,
            PaperSize = PaperSize.A5,
            FontSize = 36
        };

        var outcome = FullSheetScanner.Scan(book, startChapter: 2, options);
        var candidates = new List<(int End, PrintService.PageCounts Counts, PrintService.BlankSlots Blanks)>();
        for (var end = 2; end <= 11; end++)
        {
            var chapters = DocumentBuilder.SelectRange(book, 2, end);
            var counts = PrintService.CountPages(DocumentBuilder.Build(book, chapters, options), options);
            var blanks = counts.GetBlankSlots(options.PagesPerSheet, options.Duplex);
            candidates.Add((end, counts, blanks));
            if (!blanks.Any) break;
        }

        Assert.Equal(candidates.Count, outcome.Examined);
        var last = candidates[^1];
        if (!last.Blanks.Any)
        {
            var full = Assert.IsType<FullSheetScanner.Candidate>(outcome.Full);
            Assert.Equal(last.End, full.LastChapter);
            Assert.Equal(last.Counts.Pages, full.Pages);
            Assert.Equal(last.Counts.Sheets, full.Sides);
        }
        else
        {
            Assert.Null(outcome.Full);
            var expected = candidates.OrderBy(c => c.Blanks.Total).First();
            var closest = Assert.IsType<FullSheetScanner.Candidate>(outcome.Closest);
            Assert.Equal(expected.End, closest.LastChapter);
            Assert.Equal(expected.Blanks.Total, closest.Blanks.Total);
        }
    }

    [Theory]
    [InlineData(8, 2, DuplexMode.LongEdge, 1, null)]
    [InlineData(7, 2, DuplexMode.OneSided, 1, "1 blank half page at the end of the last printed side.")]
    [InlineData(5, 2, DuplexMode.ShortEdge, 2, "3 blank half pages per copy: 1 at the end of the last printed side and 2 on the back of the last sheet, which stays empty.")]
    [InlineData(3, 1, DuplexMode.LongEdge, 1, "1 blank page: the back of the last sheet stays empty.")]
    [InlineData(9, 4, DuplexMode.OneSided, 1, "3 blank quarter pages at the end of the last printed side.")]
    public void Describes_blank_slots(int pages, int perSide, DuplexMode duplex, int copies, string? expected)
    {
        var sides = (pages + perSide - 1) / perSide;
        var blanks = new PrintService.PageCounts(pages, sides).GetBlankSlots(perSide, duplex);
        Assert.Equal(expected, PrintWindow.DescribeBlankSlots(blanks, copies));
    }

    [StaFact]
    public void Rejects_invalid_arguments_and_observes_cancellation()
    {
        using var book = OpenBook(3);
        var options = new PrintOptions();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<ArgumentOutOfRangeException>(() => FullSheetScanner.Scan(book, 0, options));
        Assert.Throws<ArgumentOutOfRangeException>(() => FullSheetScanner.Scan(book, 4, options));
        Assert.Throws<ArgumentOutOfRangeException>(() => FullSheetScanner.Scan(book, 1, options, lookAhead: 0));
        Assert.Throws<OperationCanceledException>(() =>
            FullSheetScanner.Scan(book, 1, options, cancellationToken: cancellation.Token));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }
}
