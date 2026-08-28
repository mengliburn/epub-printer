using System.IO;
using System.IO.Compression;
using System.Text;

namespace EpubPrinter.Tests;

/// <summary>Builds synthetic .epub files so the pipeline can be tested without external fixtures.</summary>
internal static class SampleEpubFactory
{
    public static string CreateEpub3(string path, int chapterCount = 5, bool includeImage = true)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        files["mimetype"] = Encoding.ASCII.GetBytes("application/epub+zip");
        files["META-INF/container.xml"] = Utf8(
@"<?xml version='1.0' encoding='UTF-8'?>
<container version='1.0' xmlns='urn:oasis:names:tc:opendocument:xmlns:container'>
  <rootfiles>
    <rootfile full-path='OEBPS/content.opf' media-type='application/oebps-package+xml'/>
  </rootfiles>
</container>");

        var manifest = new StringBuilder();
        var spine = new StringBuilder();
        var navItems = new StringBuilder();

        for (var i = 1; i <= chapterCount; i++)
        {
            var href = "text/chapter" + i + ".xhtml";
            manifest.AppendLine($"    <item id='ch{i}' href='{href}' media-type='application/xhtml+xml'/>");
            spine.AppendLine($"    <itemref idref='ch{i}'/>");
            navItems.AppendLine($"      <li><a href='{href}'>Chapter {i}: The Number {i}</a></li>");
            files["OEBPS/" + href] = Utf8(ChapterHtml(i, includeImage && i == 2));
        }

        if (includeImage)
        {
            manifest.AppendLine("    <item id='img' href='images/pixel.png' media-type='image/png'/>");
            files["OEBPS/images/pixel.png"] = PngPixel();
        }

        files["OEBPS/nav.xhtml"] = Utf8(
$@"<?xml version='1.0' encoding='utf-8'?>
<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'>
<head><title>Contents</title></head>
<body>
  <nav epub:type='toc' id='toc'>
    <h1>Contents</h1>
    <ol>
{navItems}    </ol>
  </nav>
</body>
</html>");

        files["OEBPS/content.opf"] = Utf8(
$@"<?xml version='1.0' encoding='utf-8'?>
<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='bookid'>
  <metadata xmlns:dc='http://purl.org/dc/elements/1.1/'>
    <dc:identifier id='bookid'>urn:uuid:sample-book</dc:identifier>
    <dc:title>The Sample Book</dc:title>
    <dc:creator>Ada Lovelace</dc:creator>
    <dc:language>en</dc:language>
  </metadata>
  <manifest>
    <item id='nav' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/>
{manifest}  </manifest>
  <spine>
{spine}  </spine>
</package>");

        Write(path, files);
        return path;
    }

    /// <summary>A novel sized book used for performance probing.</summary>
    public static string CreateCustom(string path, int chapterCount, int paragraphsPerChapter)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        files["mimetype"] = Encoding.ASCII.GetBytes("application/epub+zip");
        files["META-INF/container.xml"] = Utf8(
@"<?xml version='1.0' encoding='UTF-8'?>
<container version='1.0' xmlns='urn:oasis:names:tc:opendocument:xmlns:container'>
  <rootfiles><rootfile full-path='OEBPS/content.opf' media-type='application/oebps-package+xml'/></rootfiles>
</container>");

        var manifest = new StringBuilder();
        var spine = new StringBuilder();
        var nav = new StringBuilder();

        for (var i = 1; i <= chapterCount; i++)
        {
            var href = "text/c" + i + ".xhtml";
            manifest.AppendLine($"    <item id='ch{i}' href='{href}' media-type='application/xhtml+xml'/>");
            spine.AppendLine($"    <itemref idref='ch{i}'/>");
            nav.AppendLine($"      <li><a href='{href}'>Chapter {i}</a></li>");

            var body = new StringBuilder();
            body.AppendLine($"<h1>Chapter {i}</h1>");
            for (var p = 1; p <= paragraphsPerChapter; p++)
            {
                body.Append("<p>");
                for (var s = 1; s <= 8; s++)
                    body.Append($"The narrator considered the matter carefully in paragraph {p} of chapter {i}, sentence {s}, and found it <em>quite</em> ordinary. ");
                body.AppendLine("</p>");
            }
            files["OEBPS/" + href] = Utf8($"<?xml version='1.0' encoding='utf-8'?>\n<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Chapter {i}</title></head><body>\n{body}</body></html>");
        }

        files["OEBPS/nav.xhtml"] = Utf8(
$@"<?xml version='1.0' encoding='utf-8'?>
<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'>
<head><title>Contents</title></head>
<body><nav epub:type='toc' id='toc'><ol>
{nav}</ol></nav></body></html>");

        files["OEBPS/content.opf"] = Utf8(
$@"<?xml version='1.0' encoding='utf-8'?>
<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='bookid'>
  <metadata xmlns:dc='http://purl.org/dc/elements/1.1/'>
    <dc:identifier id='bookid'>urn:uuid:perf</dc:identifier>
    <dc:title>Performance Book</dc:title>
  </metadata>
  <manifest>
    <item id='nav' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/>
{manifest}  </manifest>
  <spine>
{spine}  </spine>
</package>");

        Write(path, files);
        return path;
    }

    /// <summary>An EPUB 2 style book whose table of contents is an ncx file.</summary>
    public static string CreateEpub2(string path, int chapterCount = 3)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        files["mimetype"] = Encoding.ASCII.GetBytes("application/epub+zip");
        files["META-INF/container.xml"] = Utf8(
@"<?xml version='1.0' encoding='UTF-8'?>
<container version='1.0' xmlns='urn:oasis:names:tc:opendocument:xmlns:container'>
  <rootfiles><rootfile full-path='content.opf' media-type='application/oebps-package+xml'/></rootfiles>
</container>");

        var manifest = new StringBuilder();
        var spine = new StringBuilder();
        var navPoints = new StringBuilder();

        for (var i = 1; i <= chapterCount; i++)
        {
            var href = "c" + i + ".html";
            manifest.AppendLine($"    <item id='c{i}' href='{href}' media-type='application/xhtml+xml'/>");
            spine.AppendLine($"    <itemref idref='c{i}'/>");
            navPoints.AppendLine($"    <navPoint id='np{i}' playOrder='{i}'><navLabel><text>Part {i}</text></navLabel><content src='{href}'/></navPoint>");
            files[href] = Utf8(ChapterHtml(i, false));
        }

        files["toc.ncx"] = Utf8(
$@"<?xml version='1.0' encoding='UTF-8'?>
<ncx xmlns='http://www.daisy.org/z3986/2005/ncx/' version='2005-1'>
  <head/>
  <docTitle><text>Old Style Book</text></docTitle>
  <navMap>
{navPoints}  </navMap>
</ncx>");

        files["content.opf"] = Utf8(
$@"<?xml version='1.0' encoding='utf-8'?>
<package xmlns='http://www.idpf.org/2007/opf' version='2.0' unique-identifier='bookid'>
  <metadata xmlns:dc='http://purl.org/dc/elements/1.1/'>
    <dc:identifier id='bookid'>urn:uuid:old-book</dc:identifier>
    <dc:title>Old Style Book</dc:title>
    <dc:creator>Grace Hopper</dc:creator>
  </metadata>
  <manifest>
    <item id='ncx' href='toc.ncx' media-type='application/x-dtbncx+xml'/>
{manifest}  </manifest>
  <spine toc='ncx'>
{spine}  </spine>
</package>");

        Write(path, files);
        return path;
    }

    /// <summary>A book with no table of contents, so titles must come from the chapter markup.</summary>
    public static string CreateWithoutToc(string path)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["mimetype"] = Encoding.ASCII.GetBytes("application/epub+zip"),
            ["META-INF/container.xml"] = Utf8(
@"<?xml version='1.0' encoding='UTF-8'?>
<container version='1.0' xmlns='urn:oasis:names:tc:opendocument:xmlns:container'>
  <rootfiles><rootfile full-path='book.opf' media-type='application/oebps-package+xml'/></rootfiles>
</container>"),
            ["one.xhtml"] = Utf8("<html><head><title>ignored</title></head><body><h1>Derived Heading</h1><p>Body &amp; text.</p></body></html>"),
            ["two.xhtml"] = Utf8("<html><head><title>Title Element</title></head><body><p>No heading here.</p></body></html>"),
            ["book.opf"] = Utf8(
@"<?xml version='1.0' encoding='utf-8'?>
<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'>
  <metadata xmlns:dc='http://purl.org/dc/elements/1.1/'>
    <dc:identifier id='id'>x</dc:identifier>
    <dc:title>Untitled Work</dc:title>
  </metadata>
  <manifest>
    <item id='a' href='one.xhtml' media-type='application/xhtml+xml'/>
    <item id='b' href='two.xhtml' media-type='application/xhtml+xml'/>
  </manifest>
  <spine><itemref idref='a'/><itemref idref='b'/></spine>
</package>")
        };

        Write(path, files);
        return path;
    }

    private static string ChapterHtml(int number, bool withImage)
    {
        var image = withImage ? "<p><img src='../images/pixel.png' alt='pixel'/></p>" : string.Empty;
        var filler = string.Join(" ", Enumerable.Repeat($"Filler sentence for chapter {number}.", 40));

        return
$@"<?xml version='1.0' encoding='utf-8'?>
<html xmlns='http://www.w3.org/1999/xhtml'>
<head><title>Chapter {number}</title></head>
<body>
  <h1>Chapter {number}: The Number {number}</h1>
  <p>This is the <strong>first</strong> paragraph of chapter {number}, containing <em>emphasis</em>,
     an ampersand &amp; an em dash &#8212; plus     collapsing    whitespace.</p>
  <p style='text-align:center'>A centred line.</p>
  <blockquote><p>A quotation in chapter {number}.</p></blockquote>
  <ul><li>Bullet one</li><li>Bullet two</li></ul>
  <ol><li>First</li><li>Second</li></ol>
  <table><tr><th>Key</th><th>Value</th></tr><tr><td>Chapter</td><td>{number}</td></tr></table>
  <pre>code line {number}</pre>
  <hr/>
  {image}
  <p>{filler}</p>
</body>
</html>";
    }

    private static byte[] Utf8(string value) => new UTF8Encoding(false).GetBytes(value);

    private static byte[] PngPixel() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static void Write(string path, Dictionary<string, byte[]> files)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (File.Exists(path)) File.Delete(path);

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var pair in files)
        {
            // "mimetype" must be the first entry and is stored uncompressed.
            var level = pair.Key == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            var entry = zip.CreateEntry(pair.Key, level);
            using var entryStream = entry.Open();
            entryStream.Write(pair.Value, 0, pair.Value.Length);
        }
    }
}
