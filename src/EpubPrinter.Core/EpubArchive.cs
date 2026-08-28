using System.IO;
using System.IO.Compression;
using System.Text;

namespace EpubPrinter.Core;

/// <summary>
/// Thin wrapper over the epub zip container that resolves entries tolerantly
/// (case differences, url escaping and "./" / ".." segments are all handled).
/// </summary>
internal sealed class EpubArchive : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public EpubArchive(string path)
    {
        _zip = ZipFile.OpenRead(path);
        foreach (var entry in _zip.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
            _entries[Normalize(entry.FullName)] = entry;
        }
    }

    public IEnumerable<string> EntryNames => _entries.Keys;

    public bool Exists(string zipPath) => Find(zipPath) is not null;

    public byte[]? ReadAllBytes(string zipPath)
    {
        var entry = Find(zipPath);
        if (entry is null) return null;
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public string ReadAllText(string zipPath)
    {
        var bytes = ReadAllBytes(zipPath)
                    ?? throw new FileNotFoundException($"'{zipPath}' is not present in the epub container.");
        return DecodeText(bytes);
    }

    public static string DecodeText(byte[] bytes)
    {
        // Honour a BOM when present, otherwise assume UTF-8 (mandated by the epub spec).
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return new UTF8Encoding(false).GetString(bytes);
    }

    private ZipArchiveEntry? Find(string zipPath)
    {
        var key = Normalize(zipPath);
        if (_entries.TryGetValue(key, out var entry)) return entry;

        // Some books url-escape hrefs, others do not; try both spellings.
        var unescaped = Normalize(Uri.UnescapeDataString(zipPath));
        if (!string.Equals(unescaped, key, StringComparison.Ordinal) &&
            _entries.TryGetValue(unescaped, out entry))
            return entry;

        var fileName = key.Contains('/') ? key[(key.LastIndexOf('/') + 1)..] : key;
        foreach (var pair in _entries)
        {
            var name = pair.Key;
            var candidate = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
            if (string.Equals(candidate, fileName, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }
        return null;
    }

    /// <summary>Collapses separators and relative segments into a canonical zip path.</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;

        var value = path.Replace('\\', '/');
        var hash = value.IndexOf('#');
        if (hash >= 0) value = value[..hash];
        while (value.StartsWith("/", StringComparison.Ordinal)) value = value[1..];

        var stack = new List<string>();
        foreach (var segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(segment);
        }
        return string.Join('/', stack);
    }

    /// <summary>Resolves <paramref name="href"/> relative to the directory of <paramref name="baseFile"/>.</summary>
    public static string ResolveRelative(string baseFile, string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return string.Empty;
        if (href.StartsWith("/", StringComparison.Ordinal)) return Normalize(href);

        var baseDir = Normalize(baseFile);
        var slash = baseDir.LastIndexOf('/');
        baseDir = slash >= 0 ? baseDir[..slash] : string.Empty;
        return Normalize(baseDir.Length == 0 ? href : baseDir + "/" + href);
    }

    public void Dispose() => _zip.Dispose();
}
