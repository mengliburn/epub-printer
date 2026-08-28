using System.Globalization;
using System.IO;
using System.Printing;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Xps.Packaging;
using EpubPrinter.Core;

namespace EpubPrinter.App;

/// <summary>
/// The application's own print dialog. Windows' print dialog cannot show a preview for
/// WPF documents ("This app doesn't support print preview"), so the preview, the layout
/// choices and the printer selection all live here and the job is sent straight to the
/// chosen queue.
/// </summary>
public partial class PrintWindow : Window
{
    private static readonly int[] ScalePresets = { 50, 60, 70, 75, 80, 90, 100, 110, 125, 150, 200 };
    private static readonly int[] PagesPerSheetOptions = { 1, 2, 4, 6, 9 };

    private static readonly (DuplexMode Mode, string Label)[] DuplexLabels =
    {
        (DuplexMode.OneSided, "One sided"),
        (DuplexMode.LongEdge, "Double sided (flip on long edge)"),
        (DuplexMode.ShortEdge, "Double sided (flip on short edge)")
    };


    private readonly EpubBook _book;
    private readonly IReadOnlyList<EpubChapter> _chapters;
    private readonly PrintOptions _options;
    private readonly string _header;
    private readonly string _jobDescription;
    private readonly DispatcherTimer _debounce;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    private XpsDocument? _preview;
    private string? _previewPath;
    private int _renderVersion;
    private bool _ready;
    private bool _syncing;
    private bool _busy;
    private PrintService.PageCounts _counts;
    private List<DuplexMode> _duplexModes = new() { DuplexMode.OneSided };

    public PrintWindow(EpubBook book, IReadOnlyList<EpubChapter> chapters, PrintOptions options,
                       string header, string jobDescription)
    {
        InitializeComponent();

        _book = book ?? throw new ArgumentNullException(nameof(book));
        _chapters = chapters ?? throw new ArgumentNullException(nameof(chapters));
        _options = (options ?? PrintOptions.Default).Clone();
        _header = header ?? string.Empty;
        _jobDescription = string.IsNullOrWhiteSpace(jobDescription) ? "Epub Printer" : jobDescription;

        _debounce = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RenderPreview();
        };

        Title = $"Print - {_book.Title}";
        LoadSettings();
        Loaded += (_, _) => RenderPreview();
    }

    /// <summary>The options as adjusted in this window, so the main window can adopt them.</summary>
    public PrintOptions ResultOptions => _options;

    /// <summary>True when a job was actually sent to a printer.</summary>
    public bool Printed { get; private set; }

    private void LoadSettings()
    {
        _syncing = true;

        var queues = PrintService.GetPrintQueues();
        PrinterBox.ItemsSource = queues;
        if (queues.Count > 0) PrinterBox.SelectedIndex = 0;
        PrinterBox.IsEnabled = queues.Count > 0;

        PaperBox.ItemsSource = Enum.GetValues<PaperSize>();
        PaperBox.SelectedItem = _options.PaperSize;

        ScaleBox.ItemsSource = ScalePresets;
        ScaleBox.ItemStringFormat = "{0}%";
        ScaleBox.SelectedItem = ScalePresets.Contains(_options.ScalePercent) ? _options.ScalePercent : 100;

        ScaleSlider.Value = _options.ScalePercent;
        MarginSlider.Value = _options.MarginInches;
        PortraitRadio.IsChecked = !_options.Landscape;
        LandscapeRadio.IsChecked = _options.Landscape;
        CopiesBox.Text = "1";

        PagesPerSheetBox.ItemsSource = PagesPerSheetOptions;
        PagesPerSheetBox.SelectedItem = PagesPerSheetOptions.Contains(_options.PagesPerSheet)
            ? _options.PagesPerSheet
            : 1;

        UpdateDuplexOptions();

        _syncing = false;
        _ready = true;

        if (queues.Count == 0)
        {
            PrintButton.IsEnabled = false;
            SummaryText.Text = "No printers are installed. You can still preview, or use Save as XPS.";
        }
    }

    private PrintQueue? SelectedQueue => PrinterBox.SelectedItem as PrintQueue;

    /// <summary>Offers only the duplex modes the selected printer reports.</summary>
    private void UpdateDuplexOptions()
    {
        var wasSyncing = _syncing;
        _syncing = true;

        var supported = PrintService.GetSupportedDuplexModes(SelectedQueue);
        _duplexModes = DuplexLabels.Where(entry => supported.Contains(entry.Mode)).Select(entry => entry.Mode).ToList();

        DuplexBox.ItemsSource = DuplexLabels
            .Where(entry => supported.Contains(entry.Mode))
            .Select(entry => entry.Label)
            .ToList();

        var index = _duplexModes.IndexOf(_options.Duplex);
        if (index < 0)
        {
            index = 0;
            _options.Duplex = _duplexModes[0];
        }
        DuplexBox.SelectedIndex = index;

        var duplexAvailable = _duplexModes.Count > 1;
        DuplexBox.IsEnabled = duplexAvailable;
        DuplexNote.Visibility = duplexAvailable ? Visibility.Collapsed : Visibility.Visible;
        DuplexNote.Text = duplexAvailable
            ? string.Empty
            : "This printer does not report double sided support.";

        _syncing = wasSyncing;
    }

    private int Copies =>
        int.TryParse(CopiesBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var copies)
            ? Math.Clamp(copies, 1, 999)
            : 1;

    /// <summary>The sheet size to lay out for, taken from the selected printer when possible.</summary>
    private Size CurrentPageSize()
    {
        var queue = SelectedQueue;
        if (queue is null) return _options.PageSizeDiu;

        try
        {
            return PrintService.PageSizeFor(queue, PrintService.BuildTicket(queue, _options, Copies), _options);
        }
        catch (Exception)
        {
            return _options.PageSizeDiu;
        }
    }

    private void ScheduleRender()
    {
        if (!_ready || _syncing) return;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Renders straight away instead of waiting for the debounce timer.</summary>
    internal void FlushPendingRender()
    {
        _debounce.Stop();
        RenderPreview();
    }

    private void RenderPreview()
    {
        if (!_ready) return;

        var version = Interlocked.Increment(ref _renderVersion);
        var options = _options.Clone();
        var pageSize = CurrentPageSize();
        SetBusy(true, "Rendering preview...");

        RunOnWorker(() =>
        {
            string? path = null;
            var counts = new PrintService.PageCounts(0, 0);
            string? error = null;

            try
            {
                var document = DocumentBuilder.Build(_book, _chapters, options);
                path = Path.Combine(Path.GetTempPath(), $"epubprinter-preview-{Guid.NewGuid():N}.xps");
                counts = PrintService.ExportToXps(document, _header, options, path, pageSize);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            var rendered = path;
            var rendedCounts = counts;
            var failure = error;
            Post(() =>
            {
                if (Volatile.Read(ref _renderVersion) != version)
                {
                    TryDelete(rendered);
                    return;
                }
                ShowPreview(rendered, rendedCounts, failure);
            });
        });
    }

    private void ShowPreview(string? path, PrintService.PageCounts counts, string? error)
    {
        SetBusy(false, null);

        if (error is not null || path is null || !File.Exists(path))
        {
            SummaryText.Text = $"Preview failed: {error ?? "no output was produced"}";
            return;
        }

        try
        {
            Viewer.Document = null;
            _preview?.Close();
            TryDelete(_previewPath);

            _preview = new XpsDocument(path, FileAccess.Read);
            Viewer.Document = _preview.GetFixedDocumentSequence();
            _previewPath = path;
            _counts = counts;

            UpdateSummary();
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Preview failed: {ex.Message}";
            TryDelete(path);
        }
    }

    /// <summary>Describes what will come out of the printer, including sheets of paper.</summary>
    private void UpdateSummary()
    {
        if (!_ready || _counts.Pages <= 0) return;

        var parts = new List<string>
        {
            $"{_chapters.Count} chapter(s)",
            $"{_counts.Pages} pages at {_options.ScalePercent}%"
        };

        if (_options.PagesPerSheet > 1)
            parts.Add($"{_counts.Sheets} sides ({_options.PagesPerSheet} per side)");

        var paper = _counts.SheetsOfPaper(_options.Duplex) * Copies;
        var duplexNote = _options.Duplex == DuplexMode.OneSided ? string.Empty : ", double sided";
        parts.Add($"{paper} sheet(s) of paper{duplexNote}");

        if (Copies > 1) parts.Add($"{Copies} copies");

        SummaryText.Text = string.Join(" - ", parts) + ".";
    }

    private void OnPrint(object sender, RoutedEventArgs e)
    {
        var queue = SelectedQueue;
        if (queue is null || _busy) return;

        var printerName = queue.FullName;
        var options = _options.Clone();
        var copies = Copies;
        SetBusy(true, $"Sending to {printerName}...");

        RunOnWorker(() =>
        {
            string? error = null;
            try
            {
                // The queue is re-resolved here so it belongs to the printing thread.
                var target = PrintService.GetPrintQueues()
                    .FirstOrDefault(q => string.Equals(q.FullName, printerName, StringComparison.OrdinalIgnoreCase));
                if (target is null) throw new InvalidOperationException($"'{printerName}' is no longer available.");

                var document = DocumentBuilder.Build(_book, _chapters, options);
                PrintService.PrintTo(target, document, _header, options, _jobDescription, copies);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            var failure = error;
            Post(() =>
            {
                SetBusy(false, null);
                if (failure is not null)
                {
                    MessageBox.Show(this, failure, "Printing failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Printed = true;
                DialogResult = true;
                Close();
            });
        });
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnPrinterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateDuplexOptions();
        ScheduleRender();
    }

    private void OnCopiesChanged(object sender, TextChangedEventArgs e) => UpdateSummary();

    private void OnDigitsOnly(object sender, TextCompositionEventArgs e) =>
        e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyLayout();

    private void OnLayoutChanged(object sender, RoutedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        if (!_ready || _syncing) return;

        if (PaperBox.SelectedItem is PaperSize paper) _options.PaperSize = paper;
        _options.Landscape = LandscapeRadio.IsChecked == true;

        if (DuplexBox.SelectedIndex >= 0 && DuplexBox.SelectedIndex < _duplexModes.Count)
            _options.Duplex = _duplexModes[DuplexBox.SelectedIndex];

        if (PagesPerSheetBox.SelectedItem is int perSheet) _options.PagesPerSheet = perSheet;

        if (ScaleBox.SelectedItem is int scale && scale != _options.ScalePercent)
        {
            _options.ScalePercent = scale;
            _syncing = true;
            ScaleSlider.Value = scale;
            _syncing = false;
        }

        ScheduleRender();
    }

    private void OnScaleSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing) return;

        var scale = (int)Math.Round(e.NewValue);
        if (scale == _options.ScalePercent) return;
        _options.ScalePercent = scale;

        _syncing = true;
        ScaleBox.SelectedItem = ScalePresets.Contains(scale) ? scale : null;
        _syncing = false;

        ScheduleRender();
    }

    private void OnMarginChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing) return;
        _options.MarginInches = e.NewValue;
        ScheduleRender();
    }

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (message is not null) BusyText.Text = message;
        PrintButton.IsEnabled = !busy && PrinterBox.Items.Count > 0;
    }

    /// <summary>Documents must be built on an STA thread of their own to keep the window responsive.</summary>
    private static void RunOnWorker(Action work)
    {
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
            Name = "EpubPrinter print worker"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void Post(Action action)
    {
        if (_ui is not null) _ui.Post(_ => action(), null);
        else Dispatcher.BeginInvoke(action);
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Temporary previews are cleaned up by the OS when they are still locked.
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _debounce.Stop();
        Interlocked.Increment(ref _renderVersion);
        Viewer.Document = null;
        _preview?.Close();
        _preview = null;
        TryDelete(_previewPath);
        _previewPath = null;
    }
}





