using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace EpubPrinter.Core;

/// <summary>Builds a single printable <see cref="FlowDocument"/> from a range of chapters.</summary>
public static class DocumentBuilder
{
    public static FlowDocument Build(EpubBook book, IReadOnlyList<EpubChapter> chapters, PrintOptions options,
                                     IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (book is null) throw new ArgumentNullException(nameof(book));
        if (chapters is null) throw new ArgumentNullException(nameof(chapters));
        options ??= PrintOptions.Default;

        var document = CreateEmptyDocument(options);

        if (options.IncludeTitlePage)
            AddTitlePage(document, book, options);

        var first = document.Blocks.Count == 0;
        for (var i = 0; i < chapters.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chapter = chapters[i];
            var blocks = new List<Block>();

            try
            {
                var converter = new HtmlToFlowConverter(book, chapter.Href, options);
                blocks.AddRange(converter.Convert(book.ReadChapterText(chapter)));
            }
            catch (Exception ex)
            {
                blocks.Add(new Paragraph(new Run($"[This section could not be rendered: {ex.Message}]"))
                {
                    Foreground = Brushes.Firebrick
                });
            }

            // Most chapters already open with their own heading; only add one when it is missing,
            // so the title is never printed twice.
            if (options.IncludeChapterTitles && !StartsWithTitle(blocks, chapter.Title))
            {
                blocks.Insert(0, new Paragraph(new Run(chapter.Title))
                {
                    FontSize = options.FontSize * 1.6,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Left,
                    Margin = new Thickness(0, 0, 0, options.FontSize),
                    KeepWithNext = true
                });
            }

            if (blocks.Count == 0) continue;

            if (options.StartChapterOnNewPage && !first)
                blocks[0].BreakPageBefore = true;

            foreach (var block in blocks) document.Blocks.Add(block);
            first = false;
            progress?.Report(i + 1);
        }

        if (document.Blocks.Count == 0)
            document.Blocks.Add(new Paragraph(new Run("The selected chapters contain no printable content.")));

        return document;
    }

    /// <summary>True when the chapter markup already opens with a heading holding the chapter title.</summary>
    private static bool StartsWithTitle(IReadOnlyList<Block> blocks, string title)
    {
        if (blocks.Count == 0 || string.IsNullOrWhiteSpace(title)) return false;
        if (blocks[0] is not Paragraph paragraph) return false;

        var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
        return Normalize(text) == Normalize(title);

        static string Normalize(string value) =>
            new string(value.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray()).ToLowerInvariant();
    }

    public static FlowDocument CreateEmptyDocument(PrintOptions options)
    {
        options ??= PrintOptions.Default;
        var scale = options.Scale <= 0 ? 1.0 : options.Scale;
        var size = options.PageSizeDiu;
        var document = new FlowDocument
        {
            FontFamily = new FontFamily(options.FontFamily),
            FontSize = options.FontSize,
            LineHeight = options.FontSize * options.LineSpacing,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            PagePadding = new Thickness(options.MarginInches * 96.0 / scale),
            ColumnWidth = double.MaxValue,
            ColumnGap = 0,
            TextAlignment = options.Justify ? TextAlignment.Justify : TextAlignment.Left,
            PageWidth = size.Width / scale,
            PageHeight = size.Height / scale,
            IsOptimalParagraphEnabled = options.HighQualityLineBreaking,
            IsHyphenationEnabled = options.HighQualityLineBreaking
        };
        return document;
    }

    private static void AddTitlePage(FlowDocument document, EpubBook book, PrintOptions options)
    {
        document.Blocks.Add(new Paragraph(new Run(book.Title))
        {
            FontSize = options.FontSize * 2.4,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, options.FontSize * 8, 0, options.FontSize)
        });

        if (!string.IsNullOrWhiteSpace(book.Author))
        {
            document.Blocks.Add(new Paragraph(new Run(book.Author))
            {
                FontSize = options.FontSize * 1.3,
                TextAlignment = TextAlignment.Center,
                FontStyle = FontStyles.Italic
            });
        }
    }

    /// <summary>Returns the chapters between the given one based numbers (inclusive).</summary>
    public static IReadOnlyList<EpubChapter> SelectRange(EpubBook book, int fromNumber, int toNumber)
    {
        if (book is null) throw new ArgumentNullException(nameof(book));
        var count = book.Chapters.Count;
        if (count == 0) return Array.Empty<EpubChapter>();

        var from = Math.Clamp(Math.Min(fromNumber, toNumber), 1, count);
        var to = Math.Clamp(Math.Max(fromNumber, toNumber), 1, count);
        return book.Chapters.Skip(from - 1).Take(to - from + 1).ToList();
    }
}
