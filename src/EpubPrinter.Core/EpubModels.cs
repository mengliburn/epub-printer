using System.IO;
namespace EpubPrinter.Core;

/// <summary>A single spine item of the book, presented to the user as a "chapter".</summary>
public sealed class EpubChapter
{
    public EpubChapter(int index, string id, string href, string title)
    {
        Index = index;
        Id = id;
        Href = href;
        Title = title;
    }

    /// <summary>Zero based position in the reading order.</summary>
    public int Index { get; }

    /// <summary>Manifest id of the spine item.</summary>
    public string Id { get; }

    /// <summary>Zip-relative path of the chapter document.</summary>
    public string Href { get; }

    /// <summary>Human readable title (from the TOC, otherwise from the document itself).</summary>
    public string Title { get; internal set; }

    /// <summary>One based number shown in the UI.</summary>
    public int Number => Index + 1;

    public string Display => $"{Number}. {Title}";

    public override string ToString() => Display;
}

public sealed class EpubBook : IDisposable
{
    private readonly EpubArchive _archive;
    private readonly object _sync = new();
    private readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource?> _imageCache =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal EpubBook(EpubArchive archive, string filePath, string title, string author,
                      IReadOnlyList<EpubChapter> chapters)
    {
        _archive = archive;
        FilePath = filePath;
        Title = title;
        Author = author;
        Chapters = chapters;
    }

    public string FilePath { get; }
    public string Title { get; }
    public string Author { get; }
    public IReadOnlyList<EpubChapter> Chapters { get; }

    /// <summary>Reads a chapter's markup as text.</summary>
    public string ReadChapterText(EpubChapter chapter)
    {
        // The zip archive is shared with the background page counter, so reads are serialised.
        lock (_sync) return _archive.ReadAllText(chapter.Href);
    }

    /// <summary>Reads any resource (image, css, ...) from the archive; null when missing.</summary>
    public byte[]? ReadResource(string zipPath)
    {
        lock (_sync) return _archive.ReadAllBytes(zipPath);
    }

    public bool ResourceExists(string zipPath)
    {
        lock (_sync) return _archive.Exists(zipPath);
    }

    /// <summary>
    /// Returns a frozen, cached bitmap for an image inside the book. Decoding is by far the most
    /// expensive part of rendering illustrated books, and the document is rebuilt often.
    /// </summary>
    public System.Windows.Media.Imaging.BitmapSource? GetImage(string zipPath)
    {
        lock (_sync)
        {
            if (_imageCache.TryGetValue(zipPath, out var cached)) return cached;

            System.Windows.Media.Imaging.BitmapSource? bitmap = null;
            try
            {
                var bytes = _archive.ReadAllBytes(zipPath);
                if (bytes is { Length: > 0 })
                {
                    var image = new System.Windows.Media.Imaging.BitmapImage();
                    image.BeginInit();
                    image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    image.StreamSource = new MemoryStream(bytes);
                    image.EndInit();
                    image.Freeze();
                    bitmap = image;
                }
            }
            catch (Exception)
            {
                bitmap = null;
            }

            _imageCache[zipPath] = bitmap;
            return bitmap;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_sync)
        {
            _imageCache.Clear();
            _archive.Dispose();
        }
    }
}
