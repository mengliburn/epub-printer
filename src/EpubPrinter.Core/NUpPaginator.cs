using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace EpubPrinter.Core;

/// <summary>
/// Packs several already laid out pages onto a single sheet ("2 pages per side"). The
/// application does this itself rather than asking the driver, so the on screen preview and
/// the XPS output show exactly what will come out of the printer.
/// </summary>
public sealed class NUpPaginator : DocumentPaginator
{
    private readonly DocumentPaginator _inner;
    private readonly int _pagesPerSheet;
    private Size _sheetSize;

    public NUpPaginator(DocumentPaginator inner, Size sheetSize, int pagesPerSheet)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _pagesPerSheet = Math.Max(1, pagesPerSheet);
        _sheetSize = sheetSize.Width > 0 && sheetSize.Height > 0 ? sheetSize : inner.PageSize;
    }

    public DocumentPaginator Inner => _inner;

    public int PagesPerSheet => _pagesPerSheet;

    public override bool IsPageCountValid => _inner.IsPageCountValid;

    public override int PageCount => (int)Math.Ceiling(_inner.PageCount / (double)_pagesPerSheet);

    public override IDocumentPaginatorSource Source => _inner.Source;

    /// <summary>The size of the sheet of paper; the packed pages are shrunk to fit it.</summary>
    public override Size PageSize
    {
        get => _sheetSize;
        set
        {
            _sheetSize = value;
            _inner.PageSize = value;
        }
    }

    public override DocumentPage GetPage(int pageNumber)
    {
        if (!_inner.IsPageCountValid) _inner.ComputePageCount();

        var first = pageNumber * _pagesPerSheet;
        if (first >= _inner.PageCount) return DocumentPage.Missing;

        var layout = SheetLayout.For(_sheetSize, _inner.PageSize, _pagesPerSheet);
        var sheet = new ContainerVisual { Transform = layout.SheetTransform };

        for (var slot = 0; slot < _pagesPerSheet; slot++)
        {
            var index = first + slot;
            if (index >= _inner.PageCount) break;

            var page = _inner.GetPage(index);
            if (page == DocumentPage.Missing) break;

            // The same page visual may be handed out again later, so detach it first.
            if (VisualTreeHelper.GetParent(page.Visual) is ContainerVisual parent)
                parent.Children.Remove(page.Visual);

            var holder = new ContainerVisual { Transform = layout.SlotTransform(slot) };
            holder.Children.Add(page.Visual);
            sheet.Children.Add(holder);
        }

        var container = new ContainerVisual();
        container.Children.Add(sheet);

        var box = new Rect(new Point(0, 0), _sheetSize);
        return new DocumentPage(container, _sheetSize, box, box);
    }

    /// <summary>Where each page goes on the sheet, and how much it has to shrink.</summary>
    internal sealed class SheetLayout
    {
        private readonly Size _pageSize;
        private readonly double _fit;
        private readonly int _columns;
        private readonly double _slotWidth;
        private readonly double _slotHeight;

        private SheetLayout(Size sheetSize, Size pageSize, int rows, int columns, bool rotate)
        {
            _pageSize = pageSize;
            _columns = columns;

            // With rotation the pages are arranged in a frame turned by 90 degrees, which is
            // how a printer fits two portrait pages onto one portrait sheet.
            var area = rotate ? new Size(sheetSize.Height, sheetSize.Width) : sheetSize;
            _slotWidth = area.Width / columns;
            _slotHeight = area.Height / rows;
            _fit = Math.Min(_slotWidth / pageSize.Width, _slotHeight / pageSize.Height);

            if (rotate)
            {
                var group = new TransformGroup();
                group.Children.Add(new RotateTransform(90));
                group.Children.Add(new TranslateTransform(sheetSize.Width, 0));
                group.Freeze();
                SheetTransform = group;
            }
            else
            {
                SheetTransform = Transform.Identity;
            }

            Rows = rows;
            Columns = columns;
            Rotated = rotate;
        }

        public Transform SheetTransform { get; }
        public int Rows { get; }
        public int Columns { get; }
        public bool Rotated { get; }
        public double Fit => _fit;

        public Transform SlotTransform(int slot)
        {
            var row = slot / _columns;
            var column = slot % _columns;

            var width = _pageSize.Width * _fit;
            var height = _pageSize.Height * _fit;
            var x = (column * _slotWidth) + ((_slotWidth - width) / 2);
            var y = (row * _slotHeight) + ((_slotHeight - height) / 2);

            var group = new TransformGroup();
            group.Children.Add(new ScaleTransform(_fit, _fit));
            group.Children.Add(new TranslateTransform(x, y));
            group.Freeze();
            return group;
        }

        /// <summary>Picks the arrangement that leaves the pages as large as possible.</summary>
        public static SheetLayout For(Size sheetSize, Size pageSize, int pagesPerSheet)
        {
            if (pageSize.Width <= 0 || pageSize.Height <= 0) pageSize = sheetSize;

            SheetLayout? best = null;
            foreach (var (rows, columns) in Grids(pagesPerSheet))
            {
                foreach (var rotate in new[] { false, true })
                {
                    var candidate = new SheetLayout(sheetSize, pageSize, rows, columns, rotate);
                    if (best is null || candidate.Fit > best.Fit) best = candidate;
                }
            }

            return best!;
        }

        private static IEnumerable<(int Rows, int Columns)> Grids(int pagesPerSheet) => pagesPerSheet switch
        {
            <= 1 => new[] { (1, 1) },
            2 => new[] { (1, 2), (2, 1) },
            <= 4 => new[] { (2, 2) },
            <= 6 => new[] { (2, 3), (3, 2) },
            _ => new[] { (3, 3) }
        };
    }
}
