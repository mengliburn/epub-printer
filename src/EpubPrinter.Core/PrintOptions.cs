using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

namespace EpubPrinter.Core;

public enum PaperSize
{
    Letter,
    Legal,
    A4,
    A5
}

/// <summary>How a job is printed onto the two sides of a sheet.</summary>
public enum DuplexMode
{
    /// <summary>One page per sheet of paper.</summary>
    OneSided,

    /// <summary>Double sided, flipped along the long edge - the usual choice for portrait pages.</summary>
    LongEdge,

    /// <summary>Double sided, flipped along the short edge - used for landscape or notepad style output.</summary>
    ShortEdge
}

/// <summary>User adjustable layout settings used when building the printable document.</summary>
public sealed class PrintOptions : INotifyPropertyChanged
{
    /// <summary>The tightest margin offered; most printers cannot print closer to the edge.</summary>
    public const double MinimumMarginInches = 0.25;

    /// <summary>The widest margin offered.</summary>
    public const double MaximumMarginInches = 2.0;

    private string _fontFamily = "Georgia";
    private double _fontSize = 12;
    private double _lineSpacing = 1.3;
    private double _paragraphSpacing = 8;
    private double _paragraphIndent;
    private bool _justify;
    private bool _includeImages = true;
    private bool _includeChapterTitles = true;
    private bool _startChapterOnNewPage = true;
    private bool _includeTitlePage;
    private bool _showPageNumbers;
    private bool _showRunningHeader = true;
    private double _marginInches = MinimumMarginInches;
    private PaperSize _paperSize = PaperSize.Letter;
    private double _maxImageWidth = 450;
    private bool _highQualityLineBreaking;
    private int _scalePercent = 100;
    private bool _landscape;
    private DuplexMode _duplex = DuplexMode.ShortEdge;
    private int _pagesPerSheet = 2;

    public static PrintOptions Default => new();

    public string FontFamily
    {
        get => _fontFamily;
        set => Set(ref _fontFamily, string.IsNullOrWhiteSpace(value) ? "Georgia" : value);
    }

    public double FontSize
    {
        get => _fontSize;
        set => Set(ref _fontSize, Math.Clamp(value, 6, 48));
    }

    public double LineSpacing
    {
        get => _lineSpacing;
        set => Set(ref _lineSpacing, Math.Clamp(value, 0.8, 3.0));
    }

    public double ParagraphSpacing
    {
        get => _paragraphSpacing;
        set => Set(ref _paragraphSpacing, Math.Clamp(value, 0, 48));
    }

    public double ParagraphIndent
    {
        get => _paragraphIndent;
        set => Set(ref _paragraphIndent, Math.Clamp(value, 0, 96));
    }

    public bool Justify
    {
        get => _justify;
        set => Set(ref _justify, value);
    }

    public bool IncludeImages
    {
        get => _includeImages;
        set => Set(ref _includeImages, value);
    }

    public bool IncludeChapterTitles
    {
        get => _includeChapterTitles;
        set => Set(ref _includeChapterTitles, value);
    }

    public bool StartChapterOnNewPage
    {
        get => _startChapterOnNewPage;
        set => Set(ref _startChapterOnNewPage, value);
    }

    public bool IncludeTitlePage
    {
        get => _includeTitlePage;
        set => Set(ref _includeTitlePage, value);
    }

    public bool ShowPageNumbers
    {
        get => _showPageNumbers;
        set => Set(ref _showPageNumbers, value);
    }

    public bool ShowRunningHeader
    {
        get => _showRunningHeader;
        set => Set(ref _showRunningHeader, value);
    }

    /// <summary>Page margin in inches, applied to every side.</summary>
    public double MarginInches
    {
        get => _marginInches;
        set => Set(ref _marginInches, Math.Clamp(value, MinimumMarginInches, MaximumMarginInches));
    }

    public PaperSize PaperSize
    {
        get => _paperSize;
        set => Set(ref _paperSize, value);
    }

    /// <summary>Largest image width in device independent units.</summary>
    public double MaxImageWidth
    {
        get => _maxImageWidth;
        set => Set(ref _maxImageWidth, Math.Clamp(value, 32, 2000));
    }

    /// <summary>
    /// Enables WPF's optimal paragraph layout and hyphenation. It looks slightly better but
    /// makes pagination roughly three times slower, so it is off by default.
    /// </summary>
    public bool HighQualityLineBreaking
    {
        get => _highQualityLineBreaking;
        set => Set(ref _highQualityLineBreaking, value);
    }

    /// <summary>
    /// Print scale as a percentage. Below 100 the text is laid out on a larger logical page
    /// and shrunk onto the sheet, which fits more words per page - exactly like a browser's
    /// "scale" setting.
    /// </summary>
    public int ScalePercent
    {
        get => _scalePercent;
        set => Set(ref _scalePercent, Math.Clamp(value, 25, 400));
    }

    /// <summary>Scale expressed as a factor, e.g. 0.75 for 75%.</summary>
    public double Scale => _scalePercent / 100.0;

    public bool Landscape
    {
        get => _landscape;
        set => Set(ref _landscape, value);
    }

    /// <summary>Whether the printer should put two pages on each sheet of paper.</summary>
    public DuplexMode Duplex
    {
        get => _duplex;
        set => Set(ref _duplex, value);
    }

    /// <summary>
    /// How many document pages are composed onto one printed side. The pages are laid out
    /// and shrunk by the application, so the preview shows exactly what comes out.
    /// </summary>
    public int PagesPerSheet
    {
        get => _pagesPerSheet;
        set => Set(ref _pagesPerSheet, value switch
        {
            <= 1 => 1,
            2 => 2,
            <= 4 => 4,
            <= 6 => 6,
            _ => 9
        });
    }

    /// <summary>Point size used for the running header and the page number line.</summary>
    public double HeaderFontSize => Math.Max(8, FontSize * 0.75);

    /// <summary>
    /// Height reserved above or below the text for a header or page numbers. Nothing is
    /// reserved when they are switched off, so the text uses the whole area between margins.
    /// </summary>
    public double DecorationBand => Math.Ceiling(HeaderFontSize * 2.0);

    /// <summary>
    /// Padding of the printed page in device independent units: the margin on every side,
    /// plus room for whichever decorations are enabled.
    /// </summary>
    public Thickness PagePadding
    {
        get
        {
            var margin = MarginInches * 96.0;
            return new Thickness(
                margin,
                margin + (ShowRunningHeader ? DecorationBand : 0),
                margin,
                margin + (ShowPageNumbers ? DecorationBand : 0));
        }
    }

    public Size PageSizeDiu
    {
        get
        {
            var size = PaperSize switch
            {
                PaperSize.Legal => new Size(8.5 * 96, 14 * 96),
                PaperSize.A4 => new Size(8.27 * 96, 11.69 * 96),
                PaperSize.A5 => new Size(5.83 * 96, 8.27 * 96),
                _ => new Size(8.5 * 96, 11 * 96)
            };
            return Landscape ? new Size(size.Height, size.Width) : size;
        }
    }

    public PrintOptions Clone() => new()
    {
        _fontFamily = _fontFamily,
        _fontSize = _fontSize,
        _lineSpacing = _lineSpacing,
        _paragraphSpacing = _paragraphSpacing,
        _paragraphIndent = _paragraphIndent,
        _justify = _justify,
        _includeImages = _includeImages,
        _includeChapterTitles = _includeChapterTitles,
        _startChapterOnNewPage = _startChapterOnNewPage,
        _includeTitlePage = _includeTitlePage,
        _showPageNumbers = _showPageNumbers,
        _showRunningHeader = _showRunningHeader,
        _marginInches = _marginInches,
        _paperSize = _paperSize,
        _maxImageWidth = _maxImageWidth,
        _highQualityLineBreaking = _highQualityLineBreaking,
        _scalePercent = _scalePercent,
        _landscape = _landscape,
        _duplex = _duplex,
        _pagesPerSheet = _pagesPerSheet
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is nameof(MarginInches) or nameof(ShowPageNumbers) or nameof(ShowRunningHeader) or nameof(FontSize))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PagePadding)));
        if (name is nameof(PaperSize) or nameof(Landscape)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageSizeDiu)));
        if (name is nameof(ScalePercent)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scale)));
    }

    /// <summary>
    /// Bumped when a default changes. Settings files written by an older version keep the
    /// user's own choices but adopt the new defaults for the keys listed in
    /// <see cref="DefaultsChangedIn2"/>.
    /// </summary>
    private const int CurrentVersion = 3;

    private static readonly HashSet<string> DefaultsChangedIn2 = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(ShowPageNumbers),
        nameof(PagesPerSheet),
        nameof(Duplex)
    };

    private static readonly HashSet<string> DefaultsChangedIn3 = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(MarginInches)
    };

    private static bool DefaultReplaces(string key, int fileVersion) =>
        (fileVersion < 2 && DefaultsChangedIn2.Contains(key)) ||
        (fileVersion < 3 && DefaultsChangedIn3.Contains(key));

    /// <summary>Persists the settings as a small ini-like file next to the application data.</summary>
    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var lines = new[]
        {
            $"Version={CurrentVersion}",
            $"FontFamily={FontFamily}",
            $"FontSize={FontSize}",
            $"LineSpacing={LineSpacing}",
            $"ParagraphSpacing={ParagraphSpacing}",
            $"ParagraphIndent={ParagraphIndent}",
            $"Justify={Justify}",
            $"IncludeImages={IncludeImages}",
            $"IncludeChapterTitles={IncludeChapterTitles}",
            $"StartChapterOnNewPage={StartChapterOnNewPage}",
            $"IncludeTitlePage={IncludeTitlePage}",
            $"ShowPageNumbers={ShowPageNumbers}",
            $"ShowRunningHeader={ShowRunningHeader}",
            $"MarginInches={MarginInches}",
            $"PaperSize={PaperSize}",
            $"HighQualityLineBreaking={HighQualityLineBreaking}",
            $"ScalePercent={ScalePercent}",
            $"Landscape={Landscape}",
            $"Duplex={Duplex}",
            $"PagesPerSheet={PagesPerSheet}"
        };
        File.WriteAllLines(path, lines);
    }

    public static PrintOptions Load(string path)
    {
        var options = new PrintOptions();
        if (!File.Exists(path)) return options;

        try
        {
            var stored = new List<(string Key, string Value)>();
            var version = 1;

            foreach (var line in File.ReadAllLines(path))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();

                if (key.Equals("Version", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out var parsed)) version = parsed;
                    continue;
                }

                stored.Add((key, value));
            }

            foreach (var (key, value) in stored)
            {
                // Older files kept the previous defaults for these; take the new ones instead.
                if (DefaultReplaces(key, version)) continue;

                switch (key)
                {
                    case nameof(FontFamily): options.FontFamily = value; break;
                    case nameof(FontSize): if (double.TryParse(value, out var fs)) options.FontSize = fs; break;
                    case nameof(LineSpacing): if (double.TryParse(value, out var ls)) options.LineSpacing = ls; break;
                    case nameof(ParagraphSpacing): if (double.TryParse(value, out var ps)) options.ParagraphSpacing = ps; break;
                    case nameof(ParagraphIndent): if (double.TryParse(value, out var pi)) options.ParagraphIndent = pi; break;
                    case nameof(Justify): if (bool.TryParse(value, out var j)) options.Justify = j; break;
                    case nameof(IncludeImages): if (bool.TryParse(value, out var im)) options.IncludeImages = im; break;
                    case nameof(IncludeChapterTitles): if (bool.TryParse(value, out var ct)) options.IncludeChapterTitles = ct; break;
                    case nameof(StartChapterOnNewPage): if (bool.TryParse(value, out var np)) options.StartChapterOnNewPage = np; break;
                    case nameof(IncludeTitlePage): if (bool.TryParse(value, out var tp)) options.IncludeTitlePage = tp; break;
                    case nameof(ShowPageNumbers): if (bool.TryParse(value, out var pn)) options.ShowPageNumbers = pn; break;
                    case nameof(ShowRunningHeader): if (bool.TryParse(value, out var rh)) options.ShowRunningHeader = rh; break;
                    case nameof(MarginInches): if (double.TryParse(value, out var mi)) options.MarginInches = mi; break;
                    case nameof(PaperSize): if (Enum.TryParse<PaperSize>(value, out var paper)) options.PaperSize = paper; break;
                    case nameof(HighQualityLineBreaking): if (bool.TryParse(value, out var hq)) options.HighQualityLineBreaking = hq; break;
                    case nameof(ScalePercent): if (int.TryParse(value, out var sp)) options.ScalePercent = sp; break;
                    case nameof(Landscape): if (bool.TryParse(value, out var land)) options.Landscape = land; break;
                    case nameof(Duplex): if (Enum.TryParse<DuplexMode>(value, out var dup)) options.Duplex = dup; break;
                    case nameof(PagesPerSheet): if (int.TryParse(value, out var pps)) options.PagesPerSheet = pps; break;
                }
            }
        }
        catch (IOException)
        {
            // Corrupt settings should never stop the app from starting.
        }

        return options;
    }
}



