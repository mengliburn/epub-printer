using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EpubPrinter.Core;

namespace EpubPrinter.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        InputBindings.Add(new KeyBinding(_viewModel.OpenCommand, Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_viewModel.PrintCommand, Key.P, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_viewModel.PreviewCommand, Key.P, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(_viewModel.RefreshCommand, Key.F5, ModifierKeys.None));

        Loaded += OnLoaded;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Supports "EpubPrinter.exe book.epub" and file associations.
        var file = Environment.GetCommandLineArgs().Skip(1)
            .FirstOrDefault(a => a.EndsWith(".epub", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        if (file is not null) _viewModel.OpenBook(file);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedEpub(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        var file = TryGetDroppedEpub(e);
        if (file is not null) _viewModel.OpenBook(file);
    }

    private static string? TryGetDroppedEpub(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        return files?.FirstOrDefault(f => f.EndsWith(".epub", StringComparison.OrdinalIgnoreCase));
    }

    private void OnUseAsStart(object sender, RoutedEventArgs e)
    {
        if (ChapterList.SelectedItem is EpubChapter chapter)
            _viewModel.SetRange(chapter.Number, Math.Max(chapter.Number, _viewModel.ToChapter));
    }

    private void OnUseAsEnd(object sender, RoutedEventArgs e)
    {
        if (ChapterList.SelectedItem is EpubChapter chapter)
            _viewModel.SetRange(Math.Min(chapter.Number, _viewModel.FromChapter), chapter.Number);
    }

    private void OnOnlyThisChapter(object sender, RoutedEventArgs e)
    {
        if (ChapterList.SelectedItem is EpubChapter chapter)
            _viewModel.SetRange(chapter.Number, chapter.Number);
    }

    private void OnUseSelectionAsRange(object sender, RoutedEventArgs e)
    {
        var numbers = ChapterList.SelectedItems.OfType<EpubChapter>().Select(c => c.Number).ToList();
        if (numbers.Count == 0) return;
        _viewModel.SetRange(numbers.Min(), numbers.Max());
    }
}

