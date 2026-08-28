using System.IO;
using System.Globalization;
using System.IO.Packaging;
using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Xps;
using System.Windows.Xps.Packaging;

namespace EpubPrinter.Core;

/// <summary>
/// Wraps a flow document paginator so a running header and page numbers are stamped
/// into the page margins of every printed page, and so the content can be printed at a
/// scale other than 100%.
/// </summary>
public sealed class HeaderFooterPaginator : DocumentPaginator
{
    private readonly DocumentPaginator _inner;
    private readonly string _header;
    private readonly bool _showHeader;
    private readonly bool _showPageNumbers;
    private readonly double _margin;
    private readonly double _fontSize;
    private readonly Typeface _typeface;
    private readonly double _pixelsPerDip;
    private readonly double _scale;
    private Size _physicalPageSize;

    public HeaderFooterPaginator(DocumentPaginator inner, string header, PrintOptions options,
                                 Size physicalPageSize, double pixelsPerDip = 1.0)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        options ??= PrintOptions.Default;
        _header = header ?? string.Empty;
        _showHeader = options.ShowRunningHeader && !string.IsNullOrWhiteSpace(header);
        _showPageNumbers = options.ShowPageNumbers;
        _margin = options.MarginInches * 96.0;
        _fontSize = options.HeaderFontSize;
        _typeface = new Typeface("Segoe UI");
        _pixelsPerDip = pixelsPerDip <= 0 ? 1.0 : pixelsPerDip;
        _scale = options.Scale <= 0 ? 1.0 : options.Scale;
        _physicalPageSize = physicalPageSize.Width > 0 && physicalPageSize.Height > 0
            ? physicalPageSize
            : options.PageSizeDiu;
    }

    public override bool IsPageCountValid => _inner.IsPageCountValid;
    public override int PageCount => _inner.PageCount;
    public override IDocumentPaginatorSource Source => _inner.Source;

    /// <summary>The wrapped flow document paginator.</summary>
    public DocumentPaginator Inner => _inner;

    /// <summary>The size of the physical sheet; the content is laid out on a scaled page.</summary>
    public override Size PageSize
    {
        get => _physicalPageSize;
        set
        {
            _physicalPageSize = value;
            _inner.PageSize = new Size(value.Width / _scale, value.Height / _scale);
        }
    }

    public override DocumentPage GetPage(int pageNumber)
    {
        var page = _inner.GetPage(pageNumber);
        if (page == DocumentPage.Missing) return page;
        if (!_showHeader && !_showPageNumbers && Math.Abs(_scale - 1.0) < 0.0001) return page;

        // The total is needed for "page x of y"; pagination is finished anyway by the
        // time a job is spooled, so computing it here costs nothing extra.
        if (_showPageNumbers && !_inner.IsPageCountValid) _inner.ComputePageCount();

        var container = new ContainerVisual();

        // A paginator may be asked for the same page more than once (print preview,
        // reprints); the cached visual must be detached from its previous container first.
        if (VisualTreeHelper.GetParent(page.Visual) is ContainerVisual previous)
            previous.Children.Remove(page.Visual);

        if (Math.Abs(_scale - 1.0) < 0.0001)
        {
            container.Children.Add(page.Visual);
        }
        else
        {
            var scaled = new ContainerVisual { Transform = new ScaleTransform(_scale, _scale) };
            scaled.Children.Add(page.Visual);
            container.Children.Add(scaled);
        }

        var overlay = new DrawingVisual();
        using (var context = overlay.RenderOpen())
        {
            // Headers and footers are drawn on the sheet itself, so they keep their size
            // whatever the content scale is. Both sit centred in their margin band, which
            // keeps the top and bottom of the page looking even.
            var width = _physicalPageSize.Width;
            var height = _physicalPageSize.Height;

            if (_showHeader)
            {
                var text = Format(_header);
                text.MaxTextWidth = Math.Max(10, width - (_margin * 2));
                text.MaxLineCount = 1;
                text.Trimming = TextTrimming.CharacterEllipsis;
                context.DrawText(text, new Point(_margin, _margin));
            }

            if (_showPageNumbers)
            {
                var count = _inner.IsPageCountValid ? _inner.PageCount : 0;
                var label = count > 0
                    ? $"Page {pageNumber + 1} of {count}"
                    : $"Page {pageNumber + 1}";
                var text = Format(label);
                var x = (width - text.Width) / 2;
                var y = height - _margin - text.Height;
                context.DrawText(text, new Point(Math.Max(0, x), Math.Max(0, y)));
            }
        }

        container.Children.Add(overlay);

        var box = new Rect(new Point(0, 0), _physicalPageSize);
        return new DocumentPage(container, _physicalPageSize, box, box);
    }


    private FormattedText Format(string text) => new(
        text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        _typeface, _fontSize, Brushes.DimGray, _pixelsPerDip);
}

public static class PrintService
{
    /// <summary>Creates the paginator used for printing, preview and xps export.</summary>
    public static DocumentPaginator CreatePaginator(FlowDocument document, string header, PrintOptions options, Size pageSize)
    {
        options ??= PrintOptions.Default;
        if (pageSize.Width <= 0 || pageSize.Height <= 0) pageSize = options.PageSizeDiu;

        // The content is laid out on a page divided by the scale factor and then shrunk
        // (or blown up) onto the real sheet, so margins stay physically the same.
        var scale = options.Scale <= 0 ? 1.0 : options.Scale;
        var logical = new Size(pageSize.Width / scale, pageSize.Height / scale);
        var padding = options.PagePadding;

        document.PageWidth = logical.Width;
        document.PageHeight = logical.Height;
        document.PagePadding = new Thickness(
            padding.Left / scale, padding.Top / scale, padding.Right / scale, padding.Bottom / scale);
        document.ColumnWidth = double.MaxValue;

        var inner = ((IDocumentPaginatorSource)document).DocumentPaginator;
        inner.PageSize = logical;
        DocumentPaginator paginator = new HeaderFooterPaginator(inner, header, options, pageSize);

        // Several pages per side are composed here rather than by the driver, so that the
        // preview, the XPS export and the printed sheet are identical.
        if (options.PagesPerSheet > 1)
            paginator = new NUpPaginator(paginator, pageSize, options.PagesPerSheet);

        return paginator;
    }

    /// <summary>Prints straight to a queue, without the system print dialog.</summary>
    public static void PrintTo(PrintQueue queue, FlowDocument document, string header, PrintOptions options,
                               string jobDescription = "Epub Printer", int copies = 1)
    {
        if (queue is null) throw new ArgumentNullException(nameof(queue));
        if (document is null) throw new ArgumentNullException(nameof(document));
        options ??= PrintOptions.Default;

        var ticket = BuildTicket(queue, options, copies);
        var pageSize = PageSizeFor(queue, ticket, options);
        var paginator = CreatePaginator(document, header, options, pageSize);

        queue.CurrentJobSettings.Description = jobDescription;
        var writer = PrintQueue.CreateXpsDocumentWriter(queue);
        writer.Write(paginator, ticket);
    }

    /// <summary>Builds a print ticket that matches the chosen layout options.</summary>
    public static PrintTicket BuildTicket(PrintQueue? queue, PrintOptions options, int copies = 1)
    {
        options ??= PrintOptions.Default;
        PrintTicket ticket;
        try
        {
            // The queue hands out a live ticket object; it must be cloned before it is
            // changed, otherwise every caller (and the user's printer defaults) is affected.
            var source = queue?.UserPrintTicket ?? queue?.DefaultPrintTicket;
            ticket = source is null ? new PrintTicket() : source.Clone();
        }
        catch (Exception)
        {
            ticket = new PrintTicket();
        }

        ticket.CopyCount = Math.Max(1, copies);
        ticket.PageOrientation = options.Landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
        ticket.Duplexing = options.Duplex switch
        {
            DuplexMode.LongEdge => Duplexing.TwoSidedLongEdge,
            DuplexMode.ShortEdge => Duplexing.TwoSidedShortEdge,
            _ => Duplexing.OneSided
        };

        var media = options.PaperSize switch
        {
            PaperSize.Legal => PageMediaSizeName.NorthAmericaLegal,
            PaperSize.A4 => PageMediaSizeName.ISOA4,
            PaperSize.A5 => PageMediaSizeName.ISOA5,
            _ => PageMediaSizeName.NorthAmericaLetter
        };
        ticket.PageMediaSize = new PageMediaSize(media);
        return ticket;
    }

    /// <summary>
    /// The sheet size to lay out for: the printer's media size when it reports one,
    /// otherwise the paper size chosen in the options.
    /// </summary>
    public static Size PageSizeFor(PrintQueue? queue, PrintTicket? ticket, PrintOptions options)
    {
        options ??= PrintOptions.Default;
        try
        {
            var media = ticket?.PageMediaSize;
            if (media?.Width is > 0 && media.Height is > 0)
            {
                var size = new Size(media.Width!.Value, media.Height!.Value);
                var landscape = ticket!.PageOrientation is PageOrientation.Landscape
                    or PageOrientation.ReverseLandscape;
                if (landscape && size.Width < size.Height) size = new Size(size.Height, size.Width);
                return size;
            }
        }
        catch (Exception)
        {
            // Fall back to the configured paper size below.
        }

        return options.PageSizeDiu;
    }

    /// <summary>Writes the document to an XPS package - used for "save a copy" and for previews.</summary>
    public static PageCounts ExportToXps(FlowDocument document, string header, PrintOptions options, string path,
                                         Size? pageSize = null)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A target path is required.", nameof(path));
        options ??= PrintOptions.Default;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (File.Exists(path)) File.Delete(path);

        var paginator = CreatePaginator(document, header, options, pageSize ?? options.PageSizeDiu);

        using (var xps = new XpsDocument(path, FileAccess.ReadWrite, CompressionOption.Normal))
        {
            var writer = XpsDocument.CreateXpsDocumentWriter(xps);
            writer.Write(paginator);
        }

        var flow = paginator is NUpPaginator nUp ? nUp.Inner : paginator;
        return new PageCounts(flow.PageCount, paginator.PageCount);
    }

    /// <summary>Document pages and the number of sheet sides they are printed on.</summary>
    public readonly record struct PageCounts(int Pages, int Sheets)
    {
        /// <summary>Sheets of paper needed, taking double sided printing into account.</summary>
        public int SheetsOfPaper(DuplexMode duplex) =>
            duplex == DuplexMode.OneSided ? Sheets : (int)Math.Ceiling(Sheets / 2.0);
    }

    /// <summary>Forces full pagination and returns the page and sheet counts.</summary>
    public static PageCounts CountPages(FlowDocument document, PrintOptions options)
    {
        options ??= PrintOptions.Default;
        var paginator = CreatePaginator(document, string.Empty, options, options.PageSizeDiu);

        var flow = paginator is NUpPaginator nUp ? nUp.Inner : paginator;
        if (flow is HeaderFooterPaginator decorated) decorated.Inner.ComputePageCount();
        else flow.ComputePageCount();

        return new PageCounts(flow.PageCount, paginator.PageCount);
    }

    /// <summary>All usable print queues, the default one first.</summary>
    public static IReadOnlyList<PrintQueue> GetPrintQueues()
    {
        try
        {
            var server = new LocalPrintServer();
            var queues = server.GetPrintQueues(new[]
            {
                EnumeratedPrintQueueTypes.Local,
                EnumeratedPrintQueueTypes.Connections
            }).ToList();

            string? defaultName = null;
            try
            {
                defaultName = LocalPrintServer.GetDefaultPrintQueue()?.FullName;
            }
            catch (Exception)
            {
                // Some machines have no default printer.
            }

            return queues
                .OrderByDescending(q => string.Equals(q.FullName, defaultName, StringComparison.OrdinalIgnoreCase))
                .ThenBy(q => q.FullName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<PrintQueue>();
        }
    }

    public static string? GetDefaultPrinterName()
    {
        try
        {
            return LocalPrintServer.GetDefaultPrintQueue()?.FullName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The duplex modes the printer reports support for.</summary>
    public static IReadOnlyList<DuplexMode> GetSupportedDuplexModes(PrintQueue? queue)
    {
        var modes = new List<DuplexMode> { DuplexMode.OneSided };
        if (queue is null) return modes;

        try
        {
            var capabilities = queue.GetPrintCapabilities();
            if (capabilities.DuplexingCapability.Contains(Duplexing.TwoSidedLongEdge)) modes.Add(DuplexMode.LongEdge);
            if (capabilities.DuplexingCapability.Contains(Duplexing.TwoSidedShortEdge)) modes.Add(DuplexMode.ShortEdge);
        }
        catch (Exception)
        {
            // Drivers that refuse to report capabilities still usually honour the ticket,
            // so both flavours are offered rather than hidden.
            modes.Add(DuplexMode.LongEdge);
            modes.Add(DuplexMode.ShortEdge);
        }

        return modes;
    }
}



