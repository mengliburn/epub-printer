using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using EpubPrinter.App;
using EpubPrinter.Core;
using Xunit;

namespace EpubPrinter.Tests;

/// <summary>End to end checks that drive the real window and view model.</summary>
public sealed class UiSmokeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "epubprinter-tests", Guid.NewGuid().ToString("N"));

    private string PathFor(string name) => Path.Combine(_folder, name);

    [StaFact]
    public void Window_loads_a_book_and_produces_a_preview()
    {
        var epub = SampleEpubFactory.CreateEpub3(PathFor("ui.epub"), chapterCount: 6);

        var window = new MainWindow();
        var vm = Assert.IsType<MainViewModel>(window.DataContext);

        // The window normally takes the page count from the (background paginating) viewer;
        // the tests measure it directly instead of showing the window.
        Assert.False(vm.ComputePageCountEagerly);
        vm.ComputePageCountEagerly = true;
        vm.PersistSettings = false;

        vm.OpenBook(epub);

        Assert.True(vm.HasBook);
        Assert.Equal(6, vm.Chapters.Count);
        Assert.Equal("The Sample Book", vm.BookTitle);
        Assert.True(vm.PrintAllChapters);
        Assert.Equal(6, vm.GetSelectedChapters().Count);
        Assert.NotNull(vm.PreviewDocument);
        Assert.True(vm.PageCount > 0);
        Assert.Contains("All 6 chapters", vm.SelectionSummary);

        var allPages = vm.PageCount;

        // Switch to a range and re-render.
        vm.SetRange(2, 3);
        vm.RefreshPreview();

        Assert.False(vm.PrintAllChapters);
        Assert.True(vm.PrintChapterRange);
        Assert.Equal(2, vm.FromChapter);
        Assert.Equal(3, vm.ToChapter);
        Assert.Equal(2, vm.GetSelectedChapters().Count);
        Assert.Equal("Chapter 2: The Number 2", vm.FromChapterItem!.Title);
        Assert.True(vm.PageCount > 0);
        Assert.True(vm.PageCount < allPages, "a two chapter range should be shorter than the whole book");
        Assert.Contains("Chapters 2-3", vm.SelectionSummary);

        vm.CloseBook();
        Assert.False(vm.HasBook);
        Assert.Empty(vm.Chapters);
        Assert.Null(vm.PreviewDocument);

        window.Close();
    }

    [StaFact]
    public void Chapter_list_and_range_boxes_are_bound_to_the_book()
    {
        var epub = SampleEpubFactory.CreateEpub3(PathFor("bound.epub"), chapterCount: 4);

        var window = new MainWindow();
        var vm = (MainViewModel)window.DataContext;
        vm.ComputePageCountEagerly = true;
        vm.PersistSettings = false;
        vm.OpenBook(epub);

        var list = (ListBox)window.FindName("ChapterList")!;
        var fromBox = (ComboBox)window.FindName("FromBox")!;
        var toBox = (ComboBox)window.FindName("ToBox")!;
        window.UpdateLayout();

        Assert.Equal(4, list.Items.Count);
        Assert.Equal("1. Chapter 1: The Number 1", ((EpubChapter)list.Items[0]).Display);
        Assert.Equal(4, fromBox.Items.Count);
        Assert.Same(vm.FromChapterItem, fromBox.SelectedItem);
        Assert.Same(vm.ToChapterItem, toBox.SelectedItem);

        // Selecting in the combo box feeds straight back into the range.
        toBox.SelectedIndex = 1;
        Assert.Equal(2, vm.ToChapter);

        vm.CloseBook();
        window.Close();
    }

    [StaFact]
    public void Preview_is_shortened_for_long_selections_but_printing_is_not()
    {
        var epub = SampleEpubFactory.CreateEpub3(PathFor("long.epub"), chapterCount: 10);

        var window = new MainWindow();
        var vm = (MainViewModel)window.DataContext;
        vm.PersistSettings = false;
        vm.OpenBook(epub);

        Assert.Equal(10, vm.GetSelectedChapters().Count);
        Assert.True(vm.HasPreviewNotice);
        Assert.Contains("first 4 of 10", vm.PreviewNotice);

        var previewText = new TextRange(vm.PreviewDocument!.ContentStart, vm.PreviewDocument.ContentEnd).Text;
        Assert.Contains("Chapter 1:", previewText);
        Assert.Contains("Chapter 4:", previewText);
        Assert.DoesNotContain("Chapter 5:", previewText);

        // A short selection is shown in full.
        vm.SetRange(2, 3);
        vm.RefreshPreview();
        Assert.False(vm.HasPreviewNotice);
        Assert.Equal(string.Empty, vm.PreviewNotice);

        vm.CloseBook();
        window.Close();
    }

    [UIFact]
    public async Task Page_count_is_computed_in_the_background_for_the_whole_selection()
    {
        var epub = SampleEpubFactory.CreateEpub3(PathFor("bg.epub"), chapterCount: 9);

        var window = new MainWindow();
        var vm = (MainViewModel)window.DataContext;
        vm.PersistSettings = false;

        // The application default: counting happens off the UI thread.
        Assert.False(vm.ComputePageCountEagerly);
        vm.OpenBook(epub);

        Assert.True(vm.HasPreviewNotice);
        Assert.Equal(0, vm.PageCount);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (vm.PageCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(vm.PageCount >= 9, $"expected the full 9 chapter count, got {vm.PageCount}; status: {vm.Status}");
        Assert.Contains("page", vm.Status);

        vm.CloseBook();
        window.Close();
    }

    [UIFact]
    public async Task Print_window_previews_the_selection_without_the_system_dialog()
    {
        var epub = SampleEpubFactory.CreateEpub3(PathFor("printwin.epub"), chapterCount: 3);
        using var book = EpubReader.Open(epub);
        var options = new PrintOptions { ScalePercent = 100 };

        var window = new PrintWindow(book, book.Chapters, options, "Header", "job");
        window.Show();

        var summary = (TextBlock)window.FindName("SummaryText")!;
        var viewer = (DocumentViewer)window.FindName("Viewer")!;
        var scaleSlider = (Slider)window.FindName("ScaleSlider")!;
        var printerBox = (ComboBox)window.FindName("PrinterBox")!;

        Assert.NotNull(printerBox);
        Assert.Equal(100, (int)scaleSlider.Value);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (viewer.Document is null && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.NotNull(viewer.Document);
        Assert.Contains("pages at 100%", summary.Text);
        Assert.False(window.Printed);

        // Changing the scale re-renders and is reported back to the caller.
        scaleSlider.Value = 50;
        Assert.Equal(50, window.ResultOptions.ScalePercent);
        window.FlushPendingRender();
        deadline = DateTime.UtcNow.AddSeconds(30);
        while (!summary.Text.Contains("at 50%") && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.Contains("at 50%", summary.Text);
        Assert.Equal(50, window.ResultOptions.ScalePercent);
        window.FlushPendingRender();

        window.Close();
    }

    [UIFact]
    public async Task Print_window_offers_double_sided_and_pages_per_side()
    {
        var epub = SampleEpubFactory.CreateEpub3(PathFor("duplexwin.epub"), chapterCount: 4);
        using var book = EpubReader.Open(epub);

        var window = new PrintWindow(book, book.Chapters, new PrintOptions { PagesPerSheet = 1 }, "Header", "job");
        window.Show();

        var summary = (TextBlock)window.FindName("SummaryText")!;
        var duplexBox = (ComboBox)window.FindName("DuplexBox")!;
        var perSheetBox = (ComboBox)window.FindName("PagesPerSheetBox")!;

        Assert.Equal(new[] { 1, 2, 4, 6, 9 }, perSheetBox.Items.Cast<int>());
        Assert.NotEmpty(duplexBox.Items);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!summary.Text.Contains("sheet(s) of paper") && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        var onePerSide = summary.Text;
        Assert.Contains("sheet(s) of paper", onePerSide);
        Assert.DoesNotContain("per side", onePerSide);

        // Two pages per side halves the sheets ...
        perSheetBox.SelectedItem = 2;
        window.FlushPendingRender();
        deadline = DateTime.UtcNow.AddSeconds(30);
        while (!summary.Text.Contains("2 per side") && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.Contains("2 per side", summary.Text);
        Assert.Equal(2, window.ResultOptions.PagesPerSheet);

        // ... and double sided halves the sheets of paper again.
        if (duplexBox.Items.Count > 1)
        {
            duplexBox.SelectedIndex = 1;
            window.FlushPendingRender();
            deadline = DateTime.UtcNow.AddSeconds(30);
            while (!summary.Text.Contains("double sided") && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            Assert.Contains("double sided", summary.Text);
            Assert.NotEqual(DuplexMode.OneSided, window.ResultOptions.Duplex);
        }

        window.Close();
    }

    [StaFact]
    public void Options_changes_are_reflected_in_the_rendered_document()
    {
        var epub = SampleEpubFactory.CreateEpub3(PathFor("options.epub"), chapterCount: 3);

        var window = new MainWindow();
        var vm = (MainViewModel)window.DataContext;
        vm.ComputePageCountEagerly = true;
        vm.PersistSettings = false;
        vm.OpenBook(epub);

        vm.Options.FontSize = 11;
        vm.Options.PaperSize = PaperSize.Letter;
        vm.Options.MarginInches = 0.75;
        vm.Options.StartChapterOnNewPage = false;
        vm.RefreshPreview();
        var normalPages = vm.PageCount;

        vm.Options.FontSize = 30;
        vm.RefreshPreview();

        Assert.True(vm.PageCount > normalPages, $"larger text should need more pages ({vm.PageCount} vs {normalPages})");
        Assert.Equal(30, vm.PreviewDocument!.FontSize);

        // Chapter breaks add pages back.
        vm.Options.FontSize = 11;
        vm.RefreshPreview();
        var withoutBreaks = vm.PageCount;
        vm.Options.StartChapterOnNewPage = true;
        vm.RefreshPreview();
        Assert.True(vm.PageCount >= withoutBreaks);
        Assert.True(vm.PageCount >= vm.GetSelectedChapters().Count);

        vm.CloseBook();
        window.Close();
    }

    [StaFact]
    public void Opening_an_invalid_file_leaves_the_app_usable()
    {
        Directory.CreateDirectory(_folder);
        var bogus = PathFor("bogus.epub");
        File.WriteAllText(bogus, "not a zip");

        var window = new MainWindow();
        var vm = (MainViewModel)window.DataContext;
        vm.ComputePageCountEagerly = true;
        vm.PersistSettings = false;

        // The view model reports the failure instead of throwing or blocking on a dialog.
        var exception = Record.Exception(() => vm.OpenBook(bogus, showErrorDialog: false));

        Assert.Null(exception);
        Assert.False(vm.HasBook);
        Assert.Contains("Could not open", vm.Status);

        window.Close();
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










