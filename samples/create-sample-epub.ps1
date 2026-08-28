# Creates samples/sample-book.epub: a small, fully original book used for manual testing.
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'sample-book.epub'),
    [int]$ChapterCount = 8,
    [int]$ParagraphsPerChapter = 6
)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$chapterTitles = @(
    'The Lighthouse at Dawn', 'A Letter From the Harbour', 'Charts and Compasses',
    'The Long Crossing', 'Storm Signals', 'The Island of Bells',
    'What the Keeper Knew', 'Homeward'
)

function New-ChapterHtml([int]$Number, [string]$Title) {
    $paragraphs = @()
    for ($p = 1; $p -le $ParagraphsPerChapter; $p++) {
        $sentences = @()
        for ($s = 1; $s -le 6; $s++) {
            $sentences += "This is sentence $s of paragraph $p in chapter $Number, written purely as sample text so the printed layout can be inspected."
        }
        $paragraphs += "  <p>" + ($sentences -join ' ') + "</p>"
    }
    $body = $paragraphs -join "`n"

    @"
<?xml version='1.0' encoding='utf-8'?>
<html xmlns='http://www.w3.org/1999/xhtml'>
<head><title>$Title</title></head>
<body>
  <h1>$Title</h1>
  <p><em>Chapter $Number</em></p>
$body
  <blockquote><p>A short quotation closing chapter $Number.</p></blockquote>
  <ul><li>A first note</li><li>A second note</li></ul>
</body>
</html>
"@
}

$files = [ordered]@{}
$files['mimetype'] = 'application/epub+zip'
$files['META-INF/container.xml'] = @"
<?xml version='1.0' encoding='UTF-8'?>
<container version='1.0' xmlns='urn:oasis:names:tc:opendocument:xmlns:container'>
  <rootfiles><rootfile full-path='OEBPS/content.opf' media-type='application/oebps-package+xml'/></rootfiles>
</container>
"@

$manifest = ''
$spine = ''
$nav = ''
for ($i = 1; $i -le $ChapterCount; $i++) {
    $title = if ($i -le $chapterTitles.Count) { $chapterTitles[$i - 1] } else { "Chapter $i" }
    $href = "text/chapter$i.xhtml"
    $manifest += "    <item id='ch$i' href='$href' media-type='application/xhtml+xml'/>`n"
    $spine += "    <itemref idref='ch$i'/>`n"
    $nav += "      <li><a href='$href'>$title</a></li>`n"
    $files["OEBPS/$href"] = New-ChapterHtml -Number $i -Title $title
}

$files['OEBPS/nav.xhtml'] = @"
<?xml version='1.0' encoding='utf-8'?>
<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'>
<head><title>Contents</title></head>
<body><nav epub:type='toc' id='toc'><h1>Contents</h1><ol>
$nav</ol></nav></body>
</html>
"@

$files['OEBPS/content.opf'] = @"
<?xml version='1.0' encoding='utf-8'?>
<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='bookid'>
  <metadata xmlns:dc='http://purl.org/dc/elements/1.1/'>
    <dc:identifier id='bookid'>urn:uuid:epub-printer-sample</dc:identifier>
    <dc:title>The Lighthouse Sample</dc:title>
    <dc:creator>Epub Printer</dc:creator>
    <dc:language>en</dc:language>
  </metadata>
  <manifest>
    <item id='nav' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/>
$manifest  </manifest>
  <spine>
$spine  </spine>
</package>
"@

$directory = Split-Path -Parent $OutputPath
if ($directory -and -not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
if (Test-Path $OutputPath) { Remove-Item $OutputPath -Force }

$stream = [System.IO.File]::Open($OutputPath, [System.IO.FileMode]::CreateNew)
$zip = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in $files.Keys) {
        $level = if ($name -eq 'mimetype') { [System.IO.Compression.CompressionLevel]::NoCompression } else { [System.IO.Compression.CompressionLevel]::Optimal }
        $entry = $zip.CreateEntry($name, $level)
        $writer = New-Object System.IO.StreamWriter($entry.Open(), (New-Object System.Text.UTF8Encoding($false)))
        $writer.Write($files[$name])
        $writer.Dispose()
    }
}
finally {
    $zip.Dispose()
    $stream.Dispose()
}

Write-Host "Created $OutputPath"

