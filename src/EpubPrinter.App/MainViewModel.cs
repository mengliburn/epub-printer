using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using EpubPrinter.Core;

namespace EpubPrinter.App;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpubPrinter", "settings.ini");

    /// <summary>
    /// How many chapters are rendered into the on screen preview. Building and paginating a
    /// whole novel costs seconds, which made every change of selection feel sluggish; the
    /// printed output always contains the complete selection.
    /// </summary>
    private const int PreviewChapterLimit = 4;

    private readonly DispatcherTimer _previewDebounce;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    private EpubBook? _book;
    private string _bookTitle = "No book loaded";
    private string _bookSubtitle = "Open an .epub file to choose the chapters you want to print.";
    private bool _printAllChapters = true;
    private int _fromChapter = 1;
    private int _toChapter = 1;
    private FlowDocument? _previewDocument;
    private string _status = "Ready.";
    private bool _isBusy;
    private int _pageCount;
    private string _previewNotice = string.Empty;
    private CancellationTokenSource? _pageCountCancellation;
    private int _pageCountVersion;

    public MainViewModel()
    {
        Options = PrintOptions.Load(SettingsPath);
        Options.PropertyChanged += (_, _) => SchedulePreview();

        _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _previewDebounce.Tick += (_, _) =>
        {
            _previewDebounce.Stop();
            RefreshPreview();
        };

        OpenCommand = new RelayCommand(OpenBookViaDialog);
        CloseCommand = new RelayCommand(CloseBook, () => _book is not null);
        PrintCommand = new RelayCommand(Print, HasSelection);
        PreviewCommand = new RelayCommand(ShowPrintPreview, HasSelection);
        ExportXpsCommand = new RelayCommand(ExportXps, HasSelection);
        RefreshCommand = new RelayCommand(RefreshPreview, HasSelection);
        SelectAllCommand = new RelayCommand(() => PrintAllChapters = true, () => _book is not null);
    }

    public ObservableCollection<EpubChapter> Chapters { get; } = new();

    public PrintOptions Options { get; }

    public IReadOnlyList<PaperSize> PaperSizes { get; } = Enum.GetValues<PaperSize>();

    public IReadOnlyList<string> FontFamilies { get; } = System.Windows.Media.Fonts.SystemFontFamilies
        .Select(f => f.Source)
        .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public RelayCommand OpenCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand PrintCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public RelayCommand ExportXpsCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand SelectAllCommand { get; }

    public string BookTitle
    {
        get => _bookTitle;
        private set => Set(ref _bookTitle, value);
    }

    public string BookSubtitle
    {
        get => _bookSubtitle;
        private set => Set(ref _bookSubtitle, value);
    }

    public bool HasBook => _book is not null;

    public bool PrintAllChapters
    {
        get => _printAllChapters;
        set
        {
            if (!Set(ref _printAllChapters, value)) return;
            OnPropertyChanged(nameof(PrintChapterRange));
            OnPropertyChanged(nameof(SelectionSummary));
            SchedulePreview();
        }
    }

    public bool PrintChapterRange
    {
        get => !_printAllChapters;
        set => PrintAllChapters = !value;
    }

    public int FromChapter
    {
        get => _fromChapter;
        set
        {
            if (!Set(ref _fromChapter, Clamp(value))) return;
            if (_toChapter < _fromChapter) ToChapter = _fromChapter;
            OnPropertyChanged(nameof(FromChapterItem));
            OnPropertyChanged(nameof(SelectionSummary));
            SchedulePreview();
        }
    }

    public int ToChapter
    {
        get => _toChapter;
        set
        {
            if (!Set(ref _toChapter, Clamp(value))) return;
            if (_fromChapter > _toChapter) FromChapter = _toChapter;
            OnPropertyChanged(nameof(ToChapterItem));
            OnPropertyChanged(nameof(SelectionSummary));
            SchedulePreview();
        }
    }

    /// <summary>Chapter object bound to the "from" combo box.</summary>
    public EpubChapter? FromChapterItem
    {
        get => ChapterAt(_fromChapter);
        set
        {
            if (value is not null) FromChapter = value.Number;
        }
    }

    /// <summary>Chapter object bound to the "to" combo box.</summary>
    public EpubChapter? ToChapterItem
    {
        get => ChapterAt(_toChapter);
        set
        {
            if (value is not null) ToChapter = value.Number;
        }
    }

    private EpubChapter? ChapterAt(int number) =>
        _book is not null && number >= 1 && number <= _book.Chapters.Count ? _book.Chapters[number - 1] : null;

    /// <summary>Sets both ends of the range at once, switching to range mode.</summary>
    public void SetRange(int from, int to)
    {
        if (_book is null) return;
        _fromChapter = Clamp(Math.Min(from, to));
        _toChapter = Clamp(Math.Max(from, to));
        _printAllChapters = false;

        OnPropertyChanged(nameof(FromChapter));
        OnPropertyChanged(nameof(ToChapter));
        OnPropertyChanged(nameof(FromChapterItem));
        OnPropertyChanged(nameof(ToChapterItem));
        OnPropertyChanged(nameof(PrintAllChapters));
        OnPropertyChanged(nameof(PrintChapterRange));
        OnPropertyChanged(nameof(SelectionSummary));
        SchedulePreview();
    }

    public FlowDocument? PreviewDocument
    {
        get => _previewDocument;
        private set => Set(ref _previewDocument, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public int PageCount
    {
        get => _pageCount;
        private set
        {
            if (Set(ref _pageCount, value)) OnPropertyChanged(nameof(PageCountText));
        }
    }

    public string PageCountText => _pageCount > 0 ? $"{_pageCount} page{(_pageCount == 1 ? string.Empty : "s")}" : string.Empty;

    /// <summary>
    /// When true the page count is computed synchronously while rendering (used by tests).
    /// The application counts pages on a background thread so the UI stays responsive.
    /// </summary>
    public bool ComputePageCountEagerly { get; set; }

    /// <summary>Explains that the preview is shortened, when it is.</summary>
    public string PreviewNotice
    {
        get => _previewNotice;
        private set
        {
            if (Set(ref _previewNotice, value)) OnPropertyChanged(nameof(HasPreviewNotice));
        }
    }

    public bool HasPreviewNotice => _previewNotice.Length > 0;

    public string SelectionSummary
    {
        get
        {
            if (_book is null) return "No chapters loaded.";
            var selection = GetSelectedChapters();
            if (selection.Count == 0) return "No chapters selected.";
            if (_printAllChapters) return $"All {selection.Count} chapters selected.";
            return selection.Count == 1
                ? $"Chapter {selection[0].Number} selected."
                : $"Chapters {selection[0].Number}-{selection[^1].Number} selected ({selection.Count} of {_book.Chapters.Count}).";
        }
    }

    public IReadOnlyList<EpubChapter> GetSelectedChapters()
    {
        if (_book is null) return Array.Empty<EpubChapter>();
        return _printAllChapters
            ? _book.Chapters
            : DocumentBuilder.SelectRange(_book, _fromChapter, _toChapter);
    }

    private bool HasSelection() => _book is not null && GetSelectedChapters().Count > 0;

    public void OpenBookViaDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open an epub book",
            Filter = "EPUB books (*.epub)|*.epub|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true) OpenBook(dialog.FileName);
    }

    public void OpenBook(string path, bool showErrorDialog = true)
    {
        try
        {
            IsBusy = true;
            var book = EpubReader.Open(path);
            _book?.Dispose();
            _book = book;

            Chapters.Clear();
            foreach (var chapter in book.Chapters) Chapters.Add(chapter);

            BookTitle = book.Title;
            BookSubtitle = string.IsNullOrWhiteSpace(book.Author)
                ? $"{book.Chapters.Count} chapters - {Path.GetFileName(path)}"
                : $"{book.Author} - {book.Chapters.Count} chapters - {Path.GetFileName(path)}";

            _fromChapter = 1;
            _toChapter = book.Chapters.Count;
            _printAllChapters = true;

            OnPropertyChanged(nameof(HasBook));
            OnPropertyChanged(nameof(FromChapter));
            OnPropertyChanged(nameof(ToChapter));
            OnPropertyChanged(nameof(FromChapterItem));
            OnPropertyChanged(nameof(ToChapterItem));
            OnPropertyChanged(nameof(PrintAllChapters));
            OnPropertyChanged(nameof(PrintChapterRange));
            OnPropertyChanged(nameof(SelectionSummary));

            Status = $"Opened '{book.Title}'.";
            RefreshPreview();
        }
        catch (Exception ex)
        {
            Status = $"Could not open the book: {ex.Message}";
            if (showErrorDialog)
                MessageBox.Show(ex.Message, "Unable to open this epub", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    public void CloseBook()
    {
        CancelPageCount();
        _book?.Dispose();
        _book = null;
        Chapters.Clear();
        PreviewDocument = null;
        PreviewNotice = string.Empty;
        PageCount = 0;
        BookTitle = "No book loaded";
        BookSubtitle = "Open an .epub file to choose the chapters you want to print.";
        Status = "Ready.";
        OnPropertyChanged(nameof(HasBook));
        OnPropertyChanged(nameof(SelectionSummary));
        RelayCommand.RaiseCanExecuteChanged();
    }

    public void SchedulePreview()
    {
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    public void RefreshPreview()
    {
        if (_book is null) return;
        var selection = GetSelectedChapters();
        if (selection.Count == 0)
        {
            CancelPageCount();
            PreviewDocument = null;
            PreviewNotice = string.Empty;
            PageCount = 0;
            return;
        }

        // Only the first few chapters are rendered on screen: building and paginating a whole
        // book takes seconds and would block the UI on every change of selection.
        var previewSelection = selection.Count > PreviewChapterLimit
            ? selection.Take(PreviewChapterLimit).ToList()
            : selection;

        try
        {
            IsBusy = true;
            Status = $"Rendering {previewSelection.Count} chapter(s)...";
            var document = DocumentBuilder.Build(_book, previewSelection, Options);
            var pageSize = Options.PageSizeDiu;
            document.PageWidth = pageSize.Width;
            document.PageHeight = pageSize.Height;
            document.PagePadding = Options.PagePadding;
            document.ColumnWidth = double.MaxValue;

            PreviewDocument = document;
            PreviewNotice = previewSelection.Count < selection.Count
                ? $"Preview shows the first {previewSelection.Count} of {selection.Count} selected chapters. Printing includes all of them."
                : string.Empty;

            if (ComputePageCountEagerly)
            {
                CancelPageCount();
                var full = selection.Count == previewSelection.Count
                    ? document
                    : DocumentBuilder.Build(_book, selection, Options);
                PageCount = PrintService.CountPages(full, Options).Pages;
                Status = $"{SelectionSummary} {PageCountText} on {Options.PaperSize} paper.";
            }
            else
            {
                PageCount = 0;
                Status = $"{SelectionSummary} Counting pages...";
                StartBackgroundPageCount(selection);
            }
        }
        catch (Exception ex)
        {
            Status = $"Rendering failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    private void CancelPageCount()
    {
        Interlocked.Increment(ref _pageCountVersion);
        _pageCountCancellation?.Cancel();
        _pageCountCancellation?.Dispose();
        _pageCountCancellation = null;
    }

    /// <summary>Runs an action back on the UI thread from the page counting thread.</summary>
    private void PostToUi(Action action)
    {
        if (_uiContext is not null) _uiContext.Post(_ => action(), null);
        else _dispatcher.BeginInvoke(action);
    }

    /// <summary>
    /// Builds and paginates the full selection on a dedicated STA thread so the exact page
    /// count can be shown without freezing the window.
    /// </summary>
    private void StartBackgroundPageCount(IReadOnlyList<EpubChapter> selection)
    {
        var book = _book;
        if (book is null) return;

        CancelPageCount();
        var cancellation = new CancellationTokenSource();
        _pageCountCancellation = cancellation;

        var version = Volatile.Read(ref _pageCountVersion);
        var options = Options.Clone();
        var chapters = selection.ToList();
        var token = cancellation.Token;

        var thread = new Thread(() =>
        {
            try
            {
                var document = DocumentBuilder.Build(book, chapters, options, cancellationToken: token);
                if (token.IsCancellationRequested) return;

                var pages = PrintService.CountPages(document, options).Pages;
                if (token.IsCancellationRequested) return;

                PostToUi(() =>
                {
                    if (Volatile.Read(ref _pageCountVersion) != version || _book != book) return;
                    PageCount = pages;
                    Status = $"{SelectionSummary} {PageCountText} on {options.PaperSize} paper.";
                });
            }
            catch (OperationCanceledException)
            {
                // A newer selection replaced this one.
            }
            catch (Exception ex)
            {
                PostToUi(() =>
                {
                    if (Volatile.Read(ref _pageCountVersion) != version || _book != book) return;
                    Status = $"{SelectionSummary} (page count unavailable: {ex.Message})";
                });
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "EpubPrinter page counter"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public void Print()
    {
        if (_book is null) return;
        var selection = GetSelectedChapters();
        if (selection.Count == 0) return;

        try
        {
            // Our own print window: Windows' dialog cannot preview WPF documents, and this
            // one also carries the scale, paper and orientation choices.
            var window = new PrintWindow(_book, selection, Options, BuildHeader(selection), $"{_book.Title} (Epub Printer)")
            {
                Owner = Application.Current?.MainWindow
            };

            var printed = window.ShowDialog() == true;
            AdoptOptions(window.ResultOptions);

            Status = printed
                ? $"Sent {selection.Count} chapter(s) to the printer."
                : "Printing was cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"Printing failed: {ex.Message}";
            MessageBox.Show(ex.Message, "Printing failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Copies layout changes made in the print window back into the main settings.</summary>
    private void AdoptOptions(PrintOptions source)
    {
        if (source is null) return;
        Options.PaperSize = source.PaperSize;
        Options.Landscape = source.Landscape;
        Options.ScalePercent = source.ScalePercent;
        Options.MarginInches = source.MarginInches;
    }

    public void ShowPrintPreview() => Print();

    public void ExportXps()
    {
        if (_book is null) return;
        var selection = GetSelectedChapters();
        if (selection.Count == 0) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the selected chapters",
            Filter = "XPS document (*.xps)|*.xps",
            FileName = SanitiseFileName($"{_book.Title} - {DescribeRange(selection)}.xps")
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            IsBusy = true;
            var document = DocumentBuilder.Build(_book, selection, Options);
            var counts = PrintService.ExportToXps(document, BuildHeader(selection), Options, dialog.FileName);
            Status = $"Saved {counts.Pages} page(s) to {dialog.FileName}.";
        }
        catch (Exception ex)
        {
            Status = $"Export failed: {ex.Message}";
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string BuildHeader(IReadOnlyList<EpubChapter> selection)
    {
        if (_book is null) return string.Empty;
        var title = string.IsNullOrWhiteSpace(_book.Author) ? _book.Title : $"{_book.Title} - {_book.Author}";
        return $"{title}   |   {DescribeRange(selection)}";
    }

    private string DescribeRange(IReadOnlyList<EpubChapter> selection)
    {
        if (_book is null || selection.Count == 0) return string.Empty;
        if (_printAllChapters || selection.Count == _book.Chapters.Count) return "all chapters";
        return selection.Count == 1
            ? $"chapter {selection[0].Number}"
            : $"chapters {selection[0].Number}-{selection[^1].Number}";
    }

    private int Clamp(int value)
    {
        var count = _book?.Chapters.Count ?? 1;
        return Math.Clamp(value, 1, Math.Max(1, count));
    }

    private static string SanitiseFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Temporary preview files are cleaned up by the OS if they are still locked.
        }
    }

    /// <summary>Set to false by tests so they never overwrite the real user's saved settings.</summary>
    public bool PersistSettings { get; set; } = true;


    public void Dispose()
    {
        _previewDebounce.Stop();
        CancelPageCount();
        if (PersistSettings)
        {
            try
            {
                Options.Save(SettingsPath);
            }
            catch (IOException)
            {
                // Saving preferences is best effort.
            }
        }
        _book?.Dispose();
        _book = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}




