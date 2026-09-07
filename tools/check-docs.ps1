$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$documents = @((Get-Item -LiteralPath (Join-Path $repoRoot 'README.md'))) + @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs') -Filter '*.md' -Recurse)
$broken = [Collections.Generic.List[string]]::new()
foreach ($document in $documents) {
    $content = Get-Content -LiteralPath $document.FullName -Raw
    foreach ($link in [regex]::Matches($content, '\[[^\]]+\]\(([^\s)]+)\)')) {
        $target = $link.Groups[1].Value.Split('#')[0]
        if (!$target -or $target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:') { continue }
        $resolved = [IO.Path]::GetFullPath((Join-Path $document.DirectoryName $target))
        if (!(Test-Path -LiteralPath $resolved)) { $broken.Add("$($document.Name): $target") }
    }
}
if ($broken.Count -gt 0) { throw ($broken -join [Environment]::NewLine) }
Write-Output "Markdown 路径检查通过：$($documents.Count) 份文档。"
