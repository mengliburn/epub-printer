namespace EpubPrinter.Core;

/// <summary>Finds the first chapter range that fills every slot on a printed sheet.</summary>
public static class FullSheetScanner
{
    public const int DefaultLookAhead = 10;

    public readonly record struct Candidate(int LastChapter, int Pages, int Sides, PrintService.BlankSlots Blanks)
    {
        public bool IsFull => !Blanks.Any;
    }

    public readonly record struct ScanOutcome(int StartChapter, Candidate? Full, Candidate? Closest, int Examined);

    public static ScanOutcome Scan(EpubBook book, int startChapter, PrintOptions options,
                                   int lookAhead = DefaultLookAhead, CancellationToken cancellationToken = default)
    {
        if (book is null) throw new ArgumentNullException(nameof(book));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (startChapter < 1 || startChapter > book.Chapters.Count)
            throw new ArgumentOutOfRangeException(nameof(startChapter));
        if (lookAhead < 1) throw new ArgumentOutOfRangeException(nameof(lookAhead));

        var last = startChapter + Math.Min(lookAhead, book.Chapters.Count - startChapter + 1) - 1;
        Candidate? closest = null;
        var examined = 0;

        for (var end = startChapter; end <= last; end++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chapters = DocumentBuilder.SelectRange(book, startChapter, end);
            var document = DocumentBuilder.Build(book, chapters, options, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var counts = PrintService.CountPages(document, options);
            cancellationToken.ThrowIfCancellationRequested();
            if (counts.Pages <= 0) throw new InvalidOperationException("The selected chapters produced no pages.");

            var candidate = new Candidate(end, counts.Pages, counts.Sheets,
                counts.GetBlankSlots(options.PagesPerSheet, options.Duplex));
            examined++;

            if (candidate.IsFull) return new ScanOutcome(startChapter, candidate, candidate, examined);
            if (closest is null || candidate.Blanks.Total < closest.Value.Blanks.Total) closest = candidate;
        }

        return new ScanOutcome(startChapter, null, closest, examined);
    }
}
