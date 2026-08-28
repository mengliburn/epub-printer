using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EpubPrinter.Tools.IconGenerator;

/// <summary>
/// Draws the Epub Printer application icon (an open book with a freshly printed page)
/// and packs it into a multi resolution .ico file. Run it after changing the artwork:
///   dotnet run --project tools\IconGenerator -- src\EpubPrinter.App\Assets\app.ico
/// </summary>
internal static class Program
{
    private const double Canvas = 256;
    private static readonly int[] Sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };

    [STAThread]
    private static int Main(string[] args)
    {
        var target = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine("src", "EpubPrinter.App", "Assets", "app.ico"));

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var frames = Sizes.ToDictionary(size => size, Render);
        WriteIco(target, frames);

        // Docs artwork: the large png for the readme plus a strip to review the small sizes.
        var docs = Path.GetFullPath(args.Length > 1 ? args[1] : "docs");
        Directory.CreateDirectory(docs);

        var png = Path.Combine(docs, "icon.png");
        using (var stream = File.Create(png))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frames[256]));
            encoder.Save(stream);
        }

        WritePreviewStrip(Path.Combine(docs, "icon-sizes.png"), frames);

        Console.WriteLine($"Wrote {target} ({Sizes.Length} sizes) and {png}");
        return 0;
    }

    /// <summary>Renders every size side by side, magnified, so the small ones can be checked.</summary>
    private static void WritePreviewStrip(string path, IReadOnlyDictionary<int, RenderTargetBitmap> frames)
    {
        const int cell = 96;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);

        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, cell * Sizes.Length, cell + 96 + 24));
            for (var i = 0; i < Sizes.Length; i++)
            {
                var size = Sizes[i];

                // Native size on top ...
                var native = Math.Min(size, 80);
                dc.DrawImage(frames[size], new Rect((i * cell) + ((cell - native) / 2.0), (cell - native) / 2.0, native, native));

                // ... and magnified underneath, to judge the pixels.
                dc.DrawImage(frames[size], new Rect((i * cell) + 8, cell + 4, 80, 80));

                var label = new FormattedText($"{size}px", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.Black, 1.0);
                dc.DrawText(label, new Point((i * cell) + ((cell - label.Width) / 2), cell + 90));
            }
        }

        var strip = new RenderTargetBitmap(cell * Sizes.Length, cell + 96 + 24, 96, 96, PixelFormats.Pbgra32);
        strip.Render(visual);

        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(strip));
        encoder.Save(stream);
    }

    private static RenderTargetBitmap Render(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / Canvas, size / Canvas));
            Draw(dc, DetailFor(size));
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Below 32 pixels only a bold silhouette survives, so the artwork is simplified.</summary>
    private enum Detail
    {
        Tiny,
        Small,
        Full
    }

    private static Detail DetailFor(int size) => size switch
    {
        <= 24 => Detail.Tiny,
        <= 48 => Detail.Small,
        _ => Detail.Full
    };

    private static void Draw(DrawingContext dc, Detail detail)
    {
        DrawBackground(dc);
        DrawSheet(dc, detail);
        DrawBook(dc, detail);
    }

    private static void DrawBackground(DrawingContext dc)
    {
        var background = new LinearGradientBrush(
            Color.FromRgb(0x4F, 0x46, 0xE5),
            Color.FromRgb(0x9B, 0x30, 0xE8),
            new Point(0, 0), new Point(1, 1));
        background.Freeze();

        dc.DrawRoundedRectangle(background, null, new Rect(0, 0, Canvas, Canvas), 56, 56);

        // A soft highlight across the top left keeps the tile from looking flat.
        var highlight = new LinearGradientBrush(
            Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF),
            Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
            new Point(0, 0), new Point(0.75, 1));
        highlight.Freeze();
        dc.DrawRoundedRectangle(highlight, null, new Rect(0, 0, Canvas, Canvas), 56, 56);
    }

    /// <summary>The printed page rising out of the book.</summary>
    private static void DrawSheet(DrawingContext dc, Detail detail)
    {
        var sheet = detail == Detail.Full
            ? new Rect(80, 30, 96, 124)
            : new Rect(76, 34, 104, 120);

        var shadow = new SolidColorBrush(Color.FromArgb(0x33, 0x1E, 0x1B, 0x4B));
        shadow.Freeze();
        dc.DrawRoundedRectangle(shadow, null, new Rect(sheet.X + 5, sheet.Y + 7, sheet.Width, sheet.Height), 12, 12);
        dc.DrawRoundedRectangle(Brushes.White, null, sheet, 12, 12);

        var ink = new SolidColorBrush(Color.FromRgb(0xA5, 0xAC, 0xE0));
        ink.Freeze();
        var accent = new SolidColorBrush(Color.FromRgb(0x6D, 0x4A, 0xE8));
        accent.Freeze();

        // At 24 pixels and below every mark on the page collapses into grey mush,
        // so the sheet is left blank and only the silhouette does the work.
        if (detail == Detail.Tiny) return;

        if (detail == Detail.Full)
        {
            // A heading plus body lines, the way a printed chapter starts.
            dc.DrawRoundedRectangle(accent, null, new Rect(96, 50, 50, 12), 6, 6);
            var widths = new[] { 64.0, 56.0, 64.0, 44.0 };
            for (var i = 0; i < widths.Length; i++)
                dc.DrawRoundedRectangle(ink, null, new Rect(96, 78 + (i * 18), widths[i], 8), 4, 4);
            return;
        }

        // Fewer, chunkier marks so they survive at 32 and 48 pixels.
        dc.DrawRoundedRectangle(accent, null, new Rect(92, 50, 54, 16), 8, 8);
        dc.DrawRoundedRectangle(ink, null, new Rect(92, 82, 72, 14), 7, 7);
        dc.DrawRoundedRectangle(ink, null, new Rect(92, 108, 72, 14), 7, 7);
    }

    /// <summary>The open book the pages come from.</summary>
    private static void DrawBook(DrawingContext dc, Detail detail)
    {
        var cover = new SolidColorBrush(Color.FromRgb(0x1B, 0x18, 0x42));
        cover.Freeze();
        var page = new SolidColorBrush(Color.FromRgb(0xF8, 0xF9, 0xFF));
        page.Freeze();
        var fold = new SolidColorBrush(Color.FromRgb(0xC2, 0xC8, 0xEE));
        fold.Freeze();

        var grow = detail == Detail.Full ? 9 : 12;
        var drop = detail == Detail.Full ? 11 : 14;

        dc.DrawGeometry(cover, null, BookHalf(true, grow, drop));
        dc.DrawGeometry(cover, null, BookHalf(false, grow, drop));
        dc.DrawGeometry(page, null, BookHalf(true, 0, 0));
        dc.DrawGeometry(page, null, BookHalf(false, 0, 0));

        // The spine sits in the dip between the two pages.
        var spineWidth = detail == Detail.Full ? 12 : 16;
        dc.DrawRoundedRectangle(cover, null, new Rect(128 - (spineWidth / 2.0), 184, spineWidth, 44), 5, 5);

        if (detail != Detail.Full) return;

        for (var i = 0; i < 2; i++)
        {
            dc.DrawRoundedRectangle(fold, null, new Rect(44, 190 + (i * 15), 60, 6), 3, 3);
            dc.DrawRoundedRectangle(fold, null, new Rect(152, 190 + (i * 15), 60, 6), 3, 3);
        }
    }

    /// <summary>
    /// One half of the open book: the outer edge sits high and the paper dips towards the
    /// spine, which is how an open book reads at a glance. Growing it produces the cover.
    /// </summary>
    private static Geometry BookHalf(bool left, double grow, double drop)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var outer = left ? 24 - grow : 232 + grow;
            var inner = left ? 124 : 132;
            var control = left ? 1.0 : -1.0;

            var outerTop = 174 - (grow * 0.5);
            var innerTop = 190;
            var outerBottom = 210 + drop;
            var innerBottom = 226 + drop;

            context.BeginFigure(new Point(outer, outerTop), true, true);
            context.BezierTo(
                new Point(outer + (34 * control), outerTop - 6),
                new Point(inner - (44 * control), innerTop - 8),
                new Point(inner, innerTop), true, true);
            context.LineTo(new Point(inner, innerBottom), true, true);
            context.BezierTo(
                new Point(inner - (44 * control), innerBottom - 8),
                new Point(outer + (34 * control), outerBottom - 6),
                new Point(outer, outerBottom), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static void WriteIco(string path, IReadOnlyDictionary<int, RenderTargetBitmap> frames)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        var payloads = new List<(int Size, byte[] Data)>();
        foreach (var size in Sizes)
        {
            // Small sizes stay as classic DIBs for maximum shell compatibility,
            // larger ones are png compressed as Vista and later expect.
            var data = size >= 64 ? EncodePng(frames[size]) : EncodeDib(frames[size], size);
            payloads.Add((size, data));
        }

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)payloads.Count);

        var offset = 6 + (payloads.Count * 16);
        foreach (var (size, data) in payloads)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data) in payloads) writer.Write(data);
    }

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }

    private static byte[] EncodeDib(BitmapSource bitmap, int size)
    {
        var stride = size * 4;
        var pixels = new byte[stride * size];
        bitmap.CopyPixels(pixels, stride, 0);

        var maskStride = ((size + 31) / 32) * 4;
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);

        writer.Write(40);                 // biSize
        writer.Write(size);               // biWidth
        writer.Write(size * 2);           // biHeight: colour data plus mask
        writer.Write((ushort)1);          // biPlanes
        writer.Write((ushort)32);         // biBitCount
        writer.Write(0);                  // biCompression: BI_RGB
        writer.Write((stride * size) + (maskStride * size));
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        for (var y = size - 1; y >= 0; y--) writer.Write(pixels, y * stride, stride);
        writer.Write(new byte[maskStride * size]);

        writer.Flush();
        return memory.ToArray();
    }
}
