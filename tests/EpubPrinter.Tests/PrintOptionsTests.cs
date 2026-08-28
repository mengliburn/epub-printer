using System.IO;
using EpubPrinter.Core;
using Xunit;

namespace EpubPrinter.Tests;

public sealed class PrintOptionsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "epubprinter-tests", Guid.NewGuid().ToString("N"));

    private string PathFor(string name) => Path.Combine(_folder, name);

    [Fact]
    public void Defaults_are_economical()
    {
        var options = new PrintOptions();

        Assert.False(options.ShowPageNumbers);
        Assert.Equal(2, options.PagesPerSheet);
        Assert.Equal(DuplexMode.ShortEdge, options.Duplex);
        Assert.Equal(PrintOptions.MinimumMarginInches, options.MarginInches);
        Assert.True(options.ShowRunningHeader);
    }

    [Fact]
    public void Margins_are_clamped_to_the_offered_range()
    {
        var options = new PrintOptions { MarginInches = 0.01 };
        Assert.Equal(PrintOptions.MinimumMarginInches, options.MarginInches);

        options.MarginInches = 99;
        Assert.Equal(PrintOptions.MaximumMarginInches, options.MarginInches);
    }

    [Fact]
    public void Room_is_reserved_only_for_the_decorations_that_are_switched_on()
    {
        var margin = 0.5 * 96;
        var bare = new PrintOptions { MarginInches = 0.5, ShowPageNumbers = false, ShowRunningHeader = false };
        var numbered = new PrintOptions { MarginInches = 0.5, ShowPageNumbers = true, ShowRunningHeader = false };
        var headed = new PrintOptions { MarginInches = 0.5, ShowPageNumbers = false, ShowRunningHeader = true };

        Assert.Equal(margin, bare.PagePadding.Top, 3);
        Assert.Equal(margin, bare.PagePadding.Bottom, 3);

        // The footer takes room from the bottom only ...
        Assert.Equal(margin, numbered.PagePadding.Top, 3);
        Assert.Equal(margin + numbered.DecorationBand, numbered.PagePadding.Bottom, 3);

        // ... and the header from the top only.
        Assert.Equal(margin + headed.DecorationBand, headed.PagePadding.Top, 3);
        Assert.Equal(margin, headed.PagePadding.Bottom, 3);

        // Side margins never change.
        Assert.Equal(margin, numbered.PagePadding.Left, 3);
        Assert.Equal(margin, headed.PagePadding.Right, 3);
    }

    [Fact]
    public void Settings_round_trip()
    {
        var saved = new PrintOptions
        {
            FontFamily = "Cambria",
            FontSize = 13,
            MarginInches = 1.0,
            PaperSize = PaperSize.A4,
            ShowPageNumbers = true,
            PagesPerSheet = 4,
            Duplex = DuplexMode.LongEdge,
            ScalePercent = 80,
            Landscape = true
        };

        var path = PathFor("settings.ini");
        saved.Save(path);
        var loaded = PrintOptions.Load(path);

        Assert.Equal("Cambria", loaded.FontFamily);
        Assert.Equal(13, loaded.FontSize);
        Assert.Equal(1.0, loaded.MarginInches);
        Assert.Equal(PaperSize.A4, loaded.PaperSize);
        Assert.True(loaded.ShowPageNumbers);
        Assert.Equal(4, loaded.PagesPerSheet);
        Assert.Equal(DuplexMode.LongEdge, loaded.Duplex);
        Assert.Equal(80, loaded.ScalePercent);
        Assert.True(loaded.Landscape);
    }

    [Fact]
    public void Older_settings_keep_personal_choices_but_adopt_the_new_defaults()
    {
        var path = PathFor("old-settings.ini");
        Directory.CreateDirectory(_folder);
        File.WriteAllLines(path, new[]
        {
            "FontFamily=Verdana",
            "FontSize=14",
            "MarginInches=0.5",
            "ShowPageNumbers=True",
            "PagesPerSheet=1",
            "Duplex=OneSided",
            "Justify=True"
        });

        var loaded = PrintOptions.Load(path);

        // Chosen by the user, so kept.
        Assert.Equal("Verdana", loaded.FontFamily);
        Assert.Equal(14, loaded.FontSize);
        Assert.True(loaded.Justify);

        // Defaults that changed since that file was written.
        Assert.False(loaded.ShowPageNumbers);
        Assert.Equal(2, loaded.PagesPerSheet);
        Assert.Equal(DuplexMode.ShortEdge, loaded.Duplex);
        Assert.Equal(PrintOptions.MinimumMarginInches, loaded.MarginInches);
    }

    [Fact]
    public void A_current_settings_file_is_not_migrated_again()
    {
        var path = PathFor("current.ini");
        var options = new PrintOptions { ShowPageNumbers = true, PagesPerSheet = 1, Duplex = DuplexMode.OneSided, MarginInches = 1.5 };
        options.Save(path);

        var loaded = PrintOptions.Load(path);

        Assert.True(loaded.ShowPageNumbers);
        Assert.Equal(1, loaded.PagesPerSheet);
        Assert.Equal(DuplexMode.OneSided, loaded.Duplex);
        Assert.Equal(1.5, loaded.MarginInches);
    }

    [Fact]
    public void Missing_or_broken_settings_fall_back_to_defaults()
    {
        Assert.False(PrintOptions.Load(PathFor("nothing-here.ini")).ShowPageNumbers);

        Directory.CreateDirectory(_folder);
        var broken = PathFor("broken.ini");
        File.WriteAllText(broken, "this is not a settings file\n=\nFontSize=banana\n");

        var loaded = PrintOptions.Load(broken);
        Assert.Equal(12, loaded.FontSize);
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
