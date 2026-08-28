using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EpubPrinter.Core;
using Xunit;

namespace EpubPrinter.Tests;

/// <summary>
/// Renders real pages and measures where the ink actually lands. This is the only reliable
/// way to catch a header or page number creeping into the margin.
/// </summary>
public sealed class PageMarginTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "epubprinter-tests", Guid.NewGuid().ToString("N"));

    private string PathFor(string name) => Path.Combine(_folder, name);

    private EpubBook OpenBook(string name, int chapters = 3)
    {
        var epub = PathFor(name);
        SampleEpubFactory.CreateCustom(epub, chapters, paragraphsPerChapter: 40);
        return EpubReader.Open(epub);
    }

    private readonly record struct Ink(double Top, double Bottom, double Left, double Right, int LastRow)
    {
        public bool IsEmpty => Top < 0;
    }

    /// <summary>
    /// Measures where ink appears on a rendered page. <paramref name="scanUntil"/> stops the
    /// scan before the footer band so body text can be compared on its own.
    /// </summary>
    private static Ink Measure(DocumentPage page, Size size, double? scanUntil = null)
    {
        var width = (int)Math.Round(size.Width);
        var height = (int)Math.Round(size.Height);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(page.Visual), null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var limit = scanUntil is null ? height : Math.Min(height, (int)Math.Round(scanUntil.Value));
        int top = -1, bottom = -1, left = width, right = -1;
        for (var y = 0; y < limit; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * stride) + (x * 4);
                if (pixels[i] > 200 && pixels[i + 1] > 200 && pixels[i + 2] > 200) continue;
                if (top < 0) top = y;
                bottom = y;
                if (x < left) left = x;
                if (x > right) right = x;
            }
        }

        return top < 0
            ? new Ink(-1, -1, -1, -1, -1)
            : new Ink(top, height - 1 - bottom, left, width - 1 - right, bottom);
    }

    private static Ink MeasurePage(EpubBook book, PrintOptions options, int pageIndex = 1, double? scanUntil = null)
    {
        var document = DocumentBuilder.Build(book, book.Chapters, options);
        var paginator = PrintService.CreatePaginator(document, "A running header", options, options.PageSizeDiu);
        paginator.ComputePageCount();
        return Measure(paginator.GetPage(pageIndex), options.PageSizeDiu, scanUntil);
    }

    [StaTheory]
    [InlineData(0.25)]
    [InlineData(0.75)]
    public void Nothing_is_printed_inside_the_margin(double marginInches)
    {
        using var book = OpenBook("margins.epub");
        var margin = marginInches * 96;
        var options = new PrintOptions
        {
            MarginInches = marginInches,
            ShowPageNumbers = true,
            ShowRunningHeader = true,
            PagesPerSheet = 1
        };

        var ink = MeasurePage(book, options);

        Assert.False(ink.IsEmpty);
        Assert.True(ink.Left >= margin - 1, $"left ink at {ink.Left}, margin {margin}");
        Assert.True(ink.Right >= margin - 1, $"right ink at {ink.Right}, margin {margin}");
        Assert.True(ink.Top >= margin - 1, $"top ink at {ink.Top}, margin {margin}");
        Assert.True(ink.Bottom >= margin - 1, $"bottom ink at {ink.Bottom}, margin {margin}");
    }

    [StaFact]
    public void The_bottom_margin_matches_the_other_sides()
    {
        using var book = OpenBook("even.epub");
        var options = new PrintOptions
        {
            MarginInches = 0.75,
            ShowPageNumbers = true,
            ShowRunningHeader = true,
            PagesPerSheet = 1
        };

        var ink = MeasurePage(book, options);

        // The page number sits on the same line as the side margins, within a couple of
        // device independent units of anti aliasing.
        Assert.InRange(ink.Bottom, ink.Left - 2, ink.Left + 6);
        Assert.InRange(ink.Top, ink.Left - 2, ink.Left + 6);
    }

    [StaFact]
    public void Switching_the_page_numbers_off_gives_the_text_more_room()
    {
        using var book = OpenBook("room.epub", chapters: 2);

        var withNumbers = new PrintOptions { ShowPageNumbers = true, ShowRunningHeader = false, PagesPerSheet = 1 };
        var without = new PrintOptions { ShowPageNumbers = false, ShowRunningHeader = false, PagesPerSheet = 1 };

        // The footer band is given back to the text column.
        var columnWithout = without.PageSizeDiu.Height - without.PagePadding.Top - without.PagePadding.Bottom;
        var columnWith = withNumbers.PageSizeDiu.Height - withNumbers.PagePadding.Top - withNumbers.PagePadding.Bottom;
        Assert.Equal(without.DecorationBand, columnWithout - columnWith, 3);

        var busy = PrintService.CountPages(DocumentBuilder.Build(book, book.Chapters, withNumbers), withNumbers);
        var bare = PrintService.CountPages(DocumentBuilder.Build(book, book.Chapters, without), without);
        Assert.True(bare.Pages <= busy.Pages, $"expected no more pages without the footer ({bare.Pages} vs {busy.Pages})");

        // Comparing body text only: with the footer switched off the last line sits lower.
        var footerTop = withNumbers.PageSizeDiu.Height - (withNumbers.MarginInches * 96) - withNumbers.DecorationBand;
        var bareInk = MeasurePage(book, without);
        var busyInk = MeasurePage(book, withNumbers, scanUntil: footerTop);

        Assert.True(bareInk.LastRow > busyInk.LastRow,
            $"text should reach further down without a footer ({bareInk.LastRow} vs {busyInk.LastRow})");
    }

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
