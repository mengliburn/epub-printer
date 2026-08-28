using System.IO;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HtmlAgilityPack;

namespace EpubPrinter.Core;

/// <summary>Converts the XHTML of an epub chapter into WPF <see cref="Block"/>s.</summary>
public sealed class HtmlToFlowConverter
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "address","article","aside","blockquote","body","center","div","dl","dd","dt","figcaption","figure",
        "footer","h1","h2","h3","h4","h5","h6","header","hr","html","li","main","nav","ol","p","pre",
        "section","table","tbody","td","tfoot","th","thead","tr","ul"
    };

    private static readonly HashSet<string> SkippedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script","style","head","link","meta","title","svg","audio","video","iframe","form","input","button","select","textarea"
    };

    private readonly EpubBook? _book;
    private readonly string _chapterHref;
    private readonly PrintOptions _options;

    public HtmlToFlowConverter(EpubBook? book, string chapterHref, PrintOptions options)
    {
        _book = book;
        _chapterHref = chapterHref ?? string.Empty;
        _options = options ?? PrintOptions.Default;
    }

    /// <summary>Parses <paramref name="html"/> and returns the blocks of its body.</summary>
    public List<Block> Convert(string html)
    {
        var blocks = new List<Block>();
        if (string.IsNullOrWhiteSpace(html)) return blocks;

        var document = new HtmlDocument { OptionDefaultStreamEncoding = Encoding.UTF8 };
        document.LoadHtml(html);

        var body = document.DocumentNode.SelectSingleNode("//body")
                   ?? document.DocumentNode.SelectSingleNode("//*[local-name()='body']")
                   ?? document.DocumentNode;

        AppendChildren(body, blocks, TextStyle.Default);
        return blocks;
    }

    private void AppendChildren(HtmlNode parent, IList<Block> blocks, TextStyle style)
    {
        List<Inline>? pending = null;

        void Flush()
        {
            if (pending is null) return;
            var paragraph = BuildParagraph(pending, style);
            if (paragraph is not null) blocks.Add(paragraph);
            pending = null;
        }

        foreach (var child in parent.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Comment) continue;
            if (child.NodeType == HtmlNodeType.Element && SkippedTags.Contains(child.Name)) continue;

            if (child.NodeType == HtmlNodeType.Element && BlockTags.Contains(child.Name))
            {
                Flush();
                AppendBlock(child, blocks, style);
                continue;
            }

            var inlines = BuildInlines(child, style);
            if (inlines.Count == 0) continue;
            pending ??= new List<Inline>();
            pending.AddRange(inlines);
        }

        Flush();
    }

    private void AppendBlock(HtmlNode node, IList<Block> blocks, TextStyle style)
    {
        switch (node.Name.ToLowerInvariant())
        {
            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
            {
                var level = node.Name[1] - '0';
                var heading = new Paragraph
                {
                    FontSize = _options.FontSize * HeadingScale(level),
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, level <= 2 ? _options.FontSize : _options.FontSize * 0.8, 0, _options.FontSize * 0.4),
                    KeepWithNext = true
                };
                foreach (var inline in BuildChildInlines(node, style)) heading.Inlines.Add(inline);
                ApplyAlignment(node, heading);
                if (heading.Inlines.Count > 0) blocks.Add(heading);
                break;
            }

            case "hr":
                blocks.Add(new BlockUIContainer(new Border
                {
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Margin = new Thickness(0, 6, 0, 6)
                })
                { Margin = new Thickness(0) });
                break;

            case "pre":
            {
                var text = HtmlEntity.DeEntitize(node.InnerText ?? string.Empty).Replace("\r\n", "\n").Trim('\n');
                var paragraph = new Paragraph(new Run(text))
                {
                    FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                    FontSize = _options.FontSize * 0.9,
                    Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4)),
                    Padding = new Thickness(6),
                    Margin = new Thickness(0, 6, 0, 6),
                    TextAlignment = TextAlignment.Left
                };
                blocks.Add(paragraph);
                break;
            }

            case "blockquote":
            {
                var inner = new List<Block>();
                AppendChildren(node, inner, style);
                if (inner.Count == 0) break;
                var section = new Section { Margin = new Thickness(_options.FontSize * 1.5, 6, _options.FontSize, 6) };
                foreach (var block in inner) section.Blocks.Add(block);
                blocks.Add(section);
                break;
            }

            case "ul":
            case "ol":
            case "dl":
            {
                var list = new List
                {
                    MarkerStyle = node.Name.Equals("ol", StringComparison.OrdinalIgnoreCase)
                        ? TextMarkerStyle.Decimal
                        : node.Name.Equals("dl", StringComparison.OrdinalIgnoreCase)
                            ? TextMarkerStyle.None
                            : TextMarkerStyle.Disc,
                    Margin = new Thickness(_options.FontSize, 4, 0, 4),
                    Padding = new Thickness(0)
                };

                foreach (var item in node.ChildNodes.Where(c => c.NodeType == HtmlNodeType.Element &&
                    (c.Name.Equals("li", StringComparison.OrdinalIgnoreCase) ||
                     c.Name.Equals("dt", StringComparison.OrdinalIgnoreCase) ||
                     c.Name.Equals("dd", StringComparison.OrdinalIgnoreCase))))
                {
                    var itemBlocks = new List<Block>();
                    AppendChildren(item, itemBlocks, style);
                    if (itemBlocks.Count == 0) continue;
                    var listItem = new ListItem();
                    foreach (var block in itemBlocks) listItem.Blocks.Add(block);
                    list.ListItems.Add(listItem);
                }

                if (list.ListItems.Count > 0) blocks.Add(list);
                break;
            }

            case "table":
                AppendTable(node, blocks, style);
                break;

            case "li":
            case "dd":
            case "dt":
            case "td":
            case "th":
            case "tr":
            case "tbody":
            case "thead":
            case "tfoot":
                // Reached only for stray markup - treat transparently.
                AppendChildren(node, blocks, style);
                break;

            default:
            {
                var childStyle = style.With(node);
                var inner = new List<Block>();
                AppendChildren(node, inner, childStyle);
                foreach (var block in inner)
                {
                    if (block is Paragraph paragraph) ApplyAlignment(node, paragraph);
                    blocks.Add(block);
                }
                break;
            }
        }
    }

    private void AppendTable(HtmlNode node, IList<Block> blocks, TextStyle style)
    {
        var rows = node.SelectNodes(".//tr");
        if (rows is null || rows.Count == 0) return;

        var columnCount = rows.Max(r => r.ChildNodes.Count(c =>
            c.NodeType == HtmlNodeType.Element &&
            (c.Name.Equals("td", StringComparison.OrdinalIgnoreCase) || c.Name.Equals("th", StringComparison.OrdinalIgnoreCase))));
        if (columnCount == 0) return;

        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 6) };
        for (var i = 0; i < columnCount; i++) table.Columns.Add(new TableColumn());

        var rowGroup = new TableRowGroup();
        foreach (var row in rows)
        {
            var tableRow = new TableRow();
            foreach (var cellNode in row.ChildNodes.Where(c => c.NodeType == HtmlNodeType.Element &&
                (c.Name.Equals("td", StringComparison.OrdinalIgnoreCase) || c.Name.Equals("th", StringComparison.OrdinalIgnoreCase))))
            {
                var isHeader = cellNode.Name.Equals("th", StringComparison.OrdinalIgnoreCase);
                var cell = new TableCell
                {
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(4),
                    FontWeight = isHeader ? FontWeights.Bold : FontWeights.Normal,
                    ColumnSpan = Math.Max(1, cellNode.GetAttributeValue("colspan", 1)),
                    RowSpan = Math.Max(1, cellNode.GetAttributeValue("rowspan", 1))
                };

                var cellBlocks = new List<Block>();
                AppendChildren(cellNode, cellBlocks, style);
                if (cellBlocks.Count == 0) cellBlocks.Add(new Paragraph());
                foreach (var block in cellBlocks)
                {
                    if (block is Paragraph p) p.Margin = new Thickness(0);
                    cell.Blocks.Add(block);
                }
                tableRow.Cells.Add(cell);
            }

            if (tableRow.Cells.Count > 0) rowGroup.Rows.Add(tableRow);
        }

        if (rowGroup.Rows.Count == 0) return;
        table.RowGroups.Add(rowGroup);
        blocks.Add(table);
    }

    private Paragraph? BuildParagraph(List<Inline> inlines, TextStyle style)
    {
        var hasContent = inlines.Any(i => i is not Run run || !string.IsNullOrWhiteSpace(run.Text));
        if (!hasContent) return null;

        // Trim leading/trailing whitespace-only runs.
        while (inlines.Count > 0 && inlines[0] is Run first && string.IsNullOrWhiteSpace(first.Text)) inlines.RemoveAt(0);
        while (inlines.Count > 0 && inlines[^1] is Run last && string.IsNullOrWhiteSpace(last.Text)) inlines.RemoveAt(inlines.Count - 1);
        if (inlines.Count == 0) return null;

        // ... and the stray spaces at the very edges of the remaining text.
        if (inlines[0] is Run leading) leading.Text = leading.Text.TrimStart();
        if (inlines[^1] is Run trailing) trailing.Text = trailing.Text.TrimEnd();

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, _options.ParagraphSpacing),
            TextIndent = _options.ParagraphIndent,
            TextAlignment = _options.Justify ? TextAlignment.Justify : TextAlignment.Left
        };
        if (style.Alignment.HasValue) paragraph.TextAlignment = style.Alignment.Value;
        foreach (var inline in inlines) paragraph.Inlines.Add(inline);
        return paragraph;
    }

    private List<Inline> BuildChildInlines(HtmlNode node, TextStyle style)
    {
        var result = new List<Inline>();
        var childStyle = style.With(node);
        foreach (var child in node.ChildNodes)
            result.AddRange(BuildInlines(child, childStyle));
        return result;
    }

    private List<Inline> BuildInlines(HtmlNode node, TextStyle style)
    {
        var inlines = new List<Inline>();

        if (node.NodeType == HtmlNodeType.Text)
        {
            var text = NormalizeWhitespace(HtmlEntity.DeEntitize(node.InnerText ?? string.Empty));
            if (text.Length > 0) inlines.Add(ApplyStyle(new Run(text), style));
            return inlines;
        }

        if (node.NodeType != HtmlNodeType.Element) return inlines;
        if (SkippedTags.Contains(node.Name)) return inlines;

        switch (node.Name.ToLowerInvariant())
        {
            case "br":
                inlines.Add(new LineBreak());
                return inlines;

            case "img":
            case "image":
            {
                var image = BuildImage(node);
                if (image is not null) inlines.Add(image);
                return inlines;
            }

            default:
                inlines.AddRange(BuildChildInlines(node, style));
                return inlines;
        }
    }

    private Inline? BuildImage(HtmlNode node)
    {
        if (!_options.IncludeImages || _book is null) return null;

        var src = node.GetAttributeValue("src", null)
                  ?? node.GetAttributeValue("xlink:href", null)
                  ?? node.GetAttributeValue("href", null);
        if (string.IsNullOrWhiteSpace(src)) return null;

        try
        {
            var path = EpubArchive.ResolveRelative(_chapterHref, Uri.UnescapeDataString(src));
            var bitmap = _book.GetImage(path);
            if (bitmap is null) return null;

            var maxWidth = _options.MaxImageWidth;
            var width = bitmap.Width > 0 ? bitmap.Width : maxWidth;
            var scale = width > maxWidth ? maxWidth / width : 1.0;

            var element = new Image
            {
                Source = bitmap,
                Width = width * scale,
                Height = (bitmap.Height > 0 ? bitmap.Height : width) * scale,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 4, 0, 4)
            };
            return new InlineUIContainer(element) { BaselineAlignment = BaselineAlignment.Bottom };
        }
        catch
        {
            return null;
        }
    }

    private static Inline ApplyStyle(Run run, TextStyle style)
    {
        if (style.Bold) run.FontWeight = FontWeights.Bold;
        if (style.Italic) run.FontStyle = FontStyles.Italic;
        if (style.Monospace) run.FontFamily = new FontFamily("Consolas, Courier New, monospace");
        if (style.Underline || style.Strikethrough)
        {
            run.TextDecorations = new TextDecorationCollection();
            if (style.Underline) run.TextDecorations.Add(TextDecorations.Underline[0]);
            if (style.Strikethrough) run.TextDecorations.Add(TextDecorations.Strikethrough[0]);
        }
        if (style.Superscript) run.BaselineAlignment = BaselineAlignment.Superscript;
        if (style.Subscript) run.BaselineAlignment = BaselineAlignment.Subscript;
        if (style.Superscript || style.Subscript) run.FontSize = Math.Max(6, run.FontSize * 0.75);
        return run;
    }

    private static void ApplyAlignment(HtmlNode node, Paragraph paragraph)
    {
        var alignment = TextStyle.ReadAlignment(node);
        if (alignment.HasValue) paragraph.TextAlignment = alignment.Value;
    }

    private static double HeadingScale(int level) => level switch
    {
        1 => 1.8,
        2 => 1.5,
        3 => 1.3,
        4 => 1.15,
        5 => 1.05,
        _ => 1.0
    };

    /// <summary>Collapses runs of whitespace the way an HTML renderer would.</summary>
    internal static string NormalizeWhitespace(string value)
    {
        if (value.Length == 0) return value;

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var c in value)
        {
            var isSpace = char.IsWhiteSpace(c) && c != '\u00A0';
            if (isSpace)
            {
                if (!lastWasSpace) builder.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                builder.Append(c);
                lastWasSpace = false;
            }
        }
        return builder.ToString();
    }

    /// <summary>Inline formatting inherited while walking the html tree.</summary>
    private readonly struct TextStyle
    {
        public static readonly TextStyle Default = new();

        private TextStyle(bool bold, bool italic, bool underline, bool strikethrough,
                          bool monospace, bool superscript, bool subscript, TextAlignment? alignment)
        {
            Bold = bold; Italic = italic; Underline = underline; Strikethrough = strikethrough;
            Monospace = monospace; Superscript = superscript; Subscript = subscript; Alignment = alignment;
        }

        public bool Bold { get; }
        public bool Italic { get; }
        public bool Underline { get; }
        public bool Strikethrough { get; }
        public bool Monospace { get; }
        public bool Superscript { get; }
        public bool Subscript { get; }
        public TextAlignment? Alignment { get; }

        public TextStyle With(HtmlNode node)
        {
            var name = node.Name.ToLowerInvariant();
            var style = node.GetAttributeValue("style", string.Empty);

            var bold = Bold || name is "b" or "strong" or "th" ||
                       style.Contains("font-weight:bold", StringComparison.OrdinalIgnoreCase);
            var italic = Italic || name is "i" or "em" or "cite" or "var" or "dfn" ||
                         style.Contains("font-style:italic", StringComparison.OrdinalIgnoreCase);
            var underline = Underline || name is "u" or "ins" ||
                            style.Contains("underline", StringComparison.OrdinalIgnoreCase);
            var strike = Strikethrough || name is "s" or "strike" or "del";
            var mono = Monospace || name is "code" or "kbd" or "samp" or "tt";
            var sup = Superscript || name == "sup";
            var sub = Subscript || name == "sub";

            return new TextStyle(bold, italic, underline, strike, mono, sup, sub, ReadAlignment(node) ?? Alignment);
        }

        public static TextAlignment? ReadAlignment(HtmlNode node)
        {
            var align = node.GetAttributeValue("align", string.Empty);
            var style = node.GetAttributeValue("style", string.Empty);
            var value = align;

            var index = style.IndexOf("text-align", StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var colon = style.IndexOf(':', index);
                if (colon >= 0)
                {
                    var end = style.IndexOf(';', colon);
                    value = (end >= 0 ? style[(colon + 1)..end] : style[(colon + 1)..]).Trim();
                }
            }

            return value.Trim().ToLower(CultureInfo.InvariantCulture) switch
            {
                "center" => TextAlignment.Center,
                "right" => TextAlignment.Right,
                "left" => TextAlignment.Left,
                "justify" => TextAlignment.Justify,
                _ => null
            };
        }
    }
}
