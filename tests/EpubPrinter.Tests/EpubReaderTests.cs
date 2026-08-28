using System.IO;
using EpubPrinter.Core;
using Xunit;

namespace EpubPrinter.Tests;

public sealed class EpubReaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "epubprinter-tests", Guid.NewGuid().ToString("N"));

    private string PathFor(string name) => Path.Combine(_folder, name);

    [Fact]
    public void Opens_epub3_and_reads_metadata_and_chapters()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("sample.epub"), chapterCount: 5));

        Assert.Equal("The Sample Book", book.Title);
        Assert.Equal("Ada Lovelace", book.Author);
        Assert.Equal(5, book.Chapters.Count);
        Assert.Equal("Chapter 1: The Number 1", book.Chapters[0].Title);
        Assert.Equal("Chapter 5: The Number 5", book.Chapters[4].Title);
        Assert.Equal(1, book.Chapters[0].Number);
        Assert.Equal("1. Chapter 1: The Number 1", book.Chapters[0].Display);
    }

    [Fact]
    public void Nav_document_is_excluded_from_the_chapter_list()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("nav.epub"), chapterCount: 3));

        Assert.All(book.Chapters, c => Assert.DoesNotContain("nav.xhtml", c.Href, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, book.Chapters.Count);
    }

    [Fact]
    public void Reads_epub2_titles_from_the_ncx()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub2(PathFor("old.epub"), chapterCount: 3));

        Assert.Equal("Old Style Book", book.Title);
        Assert.Equal(new[] { "Part 1", "Part 2", "Part 3" }, book.Chapters.Select(c => c.Title));
    }

    [Fact]
    public void Derives_titles_when_no_toc_is_present()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateWithoutToc(PathFor("notoc.epub")));

        Assert.Equal(2, book.Chapters.Count);
        Assert.Equal("Derived Heading", book.Chapters[0].Title);
        Assert.Equal("Title Element", book.Chapters[1].Title);
    }

    [Fact]
    public void Chapter_text_can_be_read_and_images_resolve()
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor("img.epub"), chapterCount: 3));

        var html = book.ReadChapterText(book.Chapters[1]);
        Assert.Contains("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(book.ReadResource("OEBPS/images/pixel.png"));
        Assert.True(book.ResourceExists("OEBPS/images/pixel.png"));
        Assert.Null(book.ReadResource("OEBPS/images/missing-file.png"));
    }

    [Fact]
    public void Rejects_files_that_are_not_epubs()
    {
        Directory.CreateDirectory(_folder);
        var bogus = PathFor("not-an-epub.epub");
        File.WriteAllText(bogus, "definitely not a zip archive");

        Assert.Throws<EpubFormatException>(() => EpubReader.Open(bogus));
        Assert.Throws<FileNotFoundException>(() => EpubReader.Open(PathFor("missing.epub")));
    }

    [Theory]
    [InlineData(1, 5, 5, 1, 5)]
    [InlineData(2, 4, 3, 2, 4)]
    [InlineData(3, 3, 1, 3, 3)]
    [InlineData(4, 2, 3, 2, 4)]   // reversed input is normalised
    [InlineData(0, 99, 5, 1, 5)]  // out of range input is clamped
    public void Select_range_returns_the_expected_chapters(int from, int to, int expectedCount, int expectedFirst, int expectedLast)
    {
        using var book = EpubReader.Open(SampleEpubFactory.CreateEpub3(PathFor($"range{from}{to}.epub"), chapterCount: 5));

        var selection = DocumentBuilder.SelectRange(book, from, to);

        Assert.Equal(expectedCount, selection.Count);
        Assert.Equal(expectedFirst, selection[0].Number);
        Assert.Equal(expectedLast, selection[^1].Number);
    }

    [Fact]
    public void Zip_paths_are_normalised()
    {
        Assert.Equal("OEBPS/images/a.png", EpubArchive.ResolveRelative("OEBPS/text/ch1.xhtml", "../images/a.png"));
        Assert.Equal("OEBPS/text/a.png", EpubArchive.ResolveRelative("OEBPS/text/ch1.xhtml", "./a.png"));
        Assert.Equal("images/a.png", EpubArchive.ResolveRelative("OEBPS/text/ch1.xhtml", "/images/a.png"));
        Assert.Equal("OEBPS/text/a.png", EpubArchive.ResolveRelative("OEBPS/text/ch1.xhtml", "a.png#anchor"));
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
