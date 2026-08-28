using System.IO;
using System.Xml.Linq;
using HtmlAgilityPack;

namespace EpubPrinter.Core;

/// <summary>Opens .epub files (EPUB 2 and 3) and exposes their reading order.</summary>
public static class EpubReader
{
    private const string ContainerPath = "META-INF/container.xml";

    public static EpubBook Open(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("A file path is required.", nameof(filePath));
        if (!File.Exists(filePath)) throw new FileNotFoundException("Epub file not found.", filePath);

        EpubArchive archive;
        try
        {
            archive = new EpubArchive(filePath);
        }
        catch (InvalidDataException ex)
        {
            throw new EpubFormatException("The file is not a valid epub (zip) container.", ex);
        }

        try
        {
            var opfPath = FindOpfPath(archive);
            var opf = XDocument.Parse(archive.ReadAllText(opfPath));

            var title = FirstMetadata(opf, "title") ?? Path.GetFileNameWithoutExtension(filePath);
            var author = FirstMetadata(opf, "creator") ?? string.Empty;

            var manifest = ReadManifest(opf, opfPath);
            var chapters = ReadSpine(opf, manifest, archive);
            if (chapters.Count == 0) chapters = FallbackChapters(archive);
            if (chapters.Count == 0)
                throw new EpubFormatException("The epub does not contain any readable chapter documents.");

            ApplyTocTitles(archive, opf, manifest, opfPath, chapters);
            FillMissingTitles(archive, chapters);

            return new EpubBook(archive, filePath, title, author, chapters);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private static string FindOpfPath(EpubArchive archive)
    {
        if (archive.Exists(ContainerPath))
        {
            var container = XDocument.Parse(archive.ReadAllText(ContainerPath));
            var fullPath = container.Descendants()
                .Where(e => e.Name.LocalName == "rootfile")
                .Select(e => (string?)e.Attribute("full-path"))
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            if (!string.IsNullOrWhiteSpace(fullPath) && archive.Exists(fullPath!))
                return EpubArchive.Normalize(fullPath!);
        }

        var opf = archive.EntryNames.FirstOrDefault(n => n.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
        if (opf is not null) return opf;

        throw new EpubFormatException("No OPF package document was found inside the epub.");
    }

    private static string? FirstMetadata(XDocument opf, string localName) =>
        opf.Descendants()
           .Where(e => e.Name.LocalName == localName && e.Parent?.Name.LocalName == "metadata")
           .Select(e => e.Value.Trim())
           .FirstOrDefault(v => v.Length > 0);

    private sealed record ManifestItem(string Id, string Href, string MediaType, string Properties);

    private static Dictionary<string, ManifestItem> ReadManifest(XDocument opf, string opfPath)
    {
        var items = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
        foreach (var element in opf.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            var id = (string?)element.Attribute("id");
            var href = (string?)element.Attribute("href");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href)) continue;

            items[id!] = new ManifestItem(
                id!,
                EpubArchive.ResolveRelative(opfPath, Uri.UnescapeDataString(href!)),
                (string?)element.Attribute("media-type") ?? string.Empty,
                (string?)element.Attribute("properties") ?? string.Empty);
        }
        return items;
    }

    private static List<EpubChapter> ReadSpine(XDocument opf, Dictionary<string, ManifestItem> manifest, EpubArchive archive)
    {
        var chapters = new List<EpubChapter>();
        var spine = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "spine");
        if (spine is null) return chapters;

        foreach (var itemRef in spine.Elements().Where(e => e.Name.LocalName == "itemref"))
        {
            var idRef = (string?)itemRef.Attribute("idref");
            if (string.IsNullOrWhiteSpace(idRef) || !manifest.TryGetValue(idRef!, out var item)) continue;
            if (item.Properties.Contains("nav", StringComparison.OrdinalIgnoreCase) &&
                spine.Elements().Count() > 1) continue;
            if (!archive.Exists(item.Href)) continue;

            chapters.Add(new EpubChapter(chapters.Count, item.Id, item.Href, string.Empty));
        }
        return chapters;
    }

    private static List<EpubChapter> FallbackChapters(EpubArchive archive)
    {
        var documents = archive.EntryNames
            .Where(IsDocument)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var chapters = new List<EpubChapter>();
        foreach (var doc in documents)
            chapters.Add(new EpubChapter(chapters.Count, doc, doc, string.Empty));
        return chapters;

        static bool IsDocument(string name) =>
            name.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Applies titles from the EPUB 3 nav document or the EPUB 2 ncx table of contents.</summary>
    private static void ApplyTocTitles(EpubArchive archive, XDocument opf,
                                       Dictionary<string, ManifestItem> manifest, string opfPath,
                                       List<EpubChapter> chapters)
    {
        var titles = ReadNavTitles(archive, manifest) ?? ReadNcxTitles(archive, opf, manifest, opfPath);
        if (titles is null || titles.Count == 0) return;

        foreach (var chapter in chapters)
        {
            if (titles.TryGetValue(chapter.Href, out var title) && !string.IsNullOrWhiteSpace(title))
                chapter.Title = title;
        }
    }

    private static Dictionary<string, string>? ReadNavTitles(EpubArchive archive, Dictionary<string, ManifestItem> manifest)
    {
        var nav = manifest.Values.FirstOrDefault(i => i.Properties.Contains("nav", StringComparison.OrdinalIgnoreCase));
        if (nav is null || !archive.Exists(nav.Href)) return null;

        var html = new HtmlDocument();
        html.LoadHtml(archive.ReadAllText(nav.Href));

        // HtmlAgilityPack's XPath engine has no namespace support, so the epub:type
        // attribute is matched by hand instead of with a prefixed predicate.
        var navNodes = html.DocumentNode.SelectNodes("//nav");
        var tocNav = navNodes?.FirstOrDefault(n =>
                         n.GetAttributeValue("epub:type", string.Empty)
                          .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .Contains("toc", StringComparer.OrdinalIgnoreCase))
                     ?? navNodes?.FirstOrDefault()
                     ?? html.DocumentNode;

        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var anchors = tocNav.SelectNodes(".//a[@href]");
        if (anchors is null) return titles;

        foreach (var anchor in anchors)
        {
            var href = anchor.GetAttributeValue("href", string.Empty);
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith("#", StringComparison.Ordinal)) continue;

            var target = EpubArchive.ResolveRelative(nav.Href, Uri.UnescapeDataString(href));
            var text = HtmlEntity.DeEntitize(anchor.InnerText ?? string.Empty).Trim();
            if (target.Length == 0 || text.Length == 0) continue;
            if (!titles.ContainsKey(target)) titles[target] = Collapse(text);
        }
        return titles;
    }

    private static Dictionary<string, string>? ReadNcxTitles(EpubArchive archive, XDocument opf,
                                                            Dictionary<string, ManifestItem> manifest, string opfPath)
    {
        var ncxHref = ResolveNcxPath(opf, manifest, opfPath, archive);
        if (ncxHref is null) return null;

        var ncx = XDocument.Parse(archive.ReadAllText(ncxHref));
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var navPoint in ncx.Descendants().Where(e => e.Name.LocalName == "navPoint"))
        {
            var src = navPoint.Descendants().FirstOrDefault(e => e.Name.LocalName == "content")?.Attribute("src")?.Value;
            var label = navPoint.Descendants().FirstOrDefault(e => e.Name.LocalName == "navLabel")?.Value;
            if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(label)) continue;

            var target = EpubArchive.ResolveRelative(ncxHref, Uri.UnescapeDataString(src!));
            if (!titles.ContainsKey(target)) titles[target] = Collapse(label!.Trim());
        }
        return titles;
    }

    private static string? ResolveNcxPath(XDocument opf, Dictionary<string, ManifestItem> manifest,
                                          string opfPath, EpubArchive archive)
    {
        var tocId = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "spine")?.Attribute("toc")?.Value;
        if (!string.IsNullOrWhiteSpace(tocId) && manifest.TryGetValue(tocId!, out var tocItem) && archive.Exists(tocItem.Href))
            return tocItem.Href;

        var byType = manifest.Values.FirstOrDefault(i =>
            i.MediaType.Equals("application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase));
        if (byType is not null && archive.Exists(byType.Href)) return byType.Href;

        var byExtension = archive.EntryNames.FirstOrDefault(n => n.EndsWith(".ncx", StringComparison.OrdinalIgnoreCase));
        return byExtension;
    }

    /// <summary>Derives a title from the chapter markup for spine items missing from the TOC.</summary>
    private static void FillMissingTitles(EpubArchive archive, List<EpubChapter> chapters)
    {
        foreach (var chapter in chapters)
        {
            if (!string.IsNullOrWhiteSpace(chapter.Title)) continue;

            string? derived = null;
            try
            {
                var html = new HtmlDocument();
                html.LoadHtml(archive.ReadAllText(chapter.Href));

                var heading = html.DocumentNode.SelectSingleNode("//h1 | //h2 | //h3 | //h4 | //h5 | //h6");
                derived = heading is not null ? heading.InnerText : html.DocumentNode.SelectSingleNode("//title")?.InnerText;
                if (derived is not null) derived = Collapse(HtmlEntity.DeEntitize(derived).Trim());

                if (string.IsNullOrWhiteSpace(derived))
                {
                    var body = html.DocumentNode.SelectSingleNode("//body") ?? html.DocumentNode;
                    var text = Collapse(HtmlEntity.DeEntitize(body.InnerText ?? string.Empty).Trim());
                    if (text.Length > 60) text = text[..60].TrimEnd() + "…";
                    derived = text;
                }
            }
            catch
            {
                // A malformed chapter should never stop the book from opening.
            }

            chapter.Title = string.IsNullOrWhiteSpace(derived)
                ? $"Section {chapter.Number}"
                : derived!;
        }
    }

    private static string Collapse(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public class EpubFormatException : Exception
{
    public EpubFormatException(string message) : base(message) { }
    public EpubFormatException(string message, Exception inner) : base(message, inner) { }
}
