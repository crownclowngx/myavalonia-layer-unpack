<#[中文说明]
本地互操作检查：使用独立 libarchive 的 bsdtar 读取本次测试生成的 ZIP/TAR/TAR.GZ，逐项核对目录、长度与 SHA-256。
7z 候选应被独立工具拒绝；这是“不开放”的证据，不是 7z 创建验收通过。所有文件只写在本仓库 artifacts 中。
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$evidenceRoot = Join-Path $repoRoot 'artifacts/format-r07'
$tarCommand = (Get-Command tar -ErrorAction Stop).Source
$version = (& $tarCommand --version 2>&1) -join ' '
if ($LASTEXITCODE -ne 0 -or $version -notmatch 'libarchive') { throw '需要独立 libarchive/bsdtar 工具验证；不使用本插件引擎自证互操作。' }
$runRoot = Join-Path $evidenceRoot ('interop-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
foreach ($name in @('sample.zip', 'sample.tar', 'sample.tar.gz')) {
    $archive = Join-Path $evidenceRoot $name
    $manifest = Get-Content -LiteralPath ($archive + '.json') -Raw | ConvertFrom-Json
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $manifest.sha256) { throw "本次生成样本摘要不符：$name。" }
    $destination = Join-Path $runRoot $name
    New-Item -ItemType Directory -Path $destination | Out-Null
    $output = & $tarCommand -xf $archive -C $destination 2>&1
    $exitCode = $LASTEXITCODE
    $output | Set-Content -LiteralPath (Join-Path $runRoot ($name + '.log')) -Encoding utf8
    if ($exitCode -ne 0) { throw "独立工具无法读取 $name，退出码 $exitCode。" }
    foreach ($entry in $manifest.entries) {
        $path = [IO.Path]::GetFullPath((Join-Path $destination $entry.path))
        if (!$path.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '互操作清单路径越界。' }
        if ($entry.directory) {
            if (!(Test-Path -LiteralPath $path -PathType Container)) { throw "目录丢失：$($entry.path)。" }
        }
        elseif (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $entry.bytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw "正文不符：$($entry.path)。" }
    }
    $actual = @(Get-ChildItem -LiteralPath $destination -Recurse -Force)
    if ($actual.Count -ne $manifest.entries.Count) { throw "$name 产生额外或缺失条目。" }
    $results.Add([pscustomobject]@{ archive = $name; sha256 = $manifest.sha256; accepted = $true; entries = $actual.Count; exitCode = $exitCode })
}
foreach ($name in @('candidate-header-True.7z', 'candidate-header-False.7z')) {
    $archive = Join-Path $evidenceRoot $name
    $output = & $tarCommand -tf $archive 2>&1
    $exitCode = $LASTEXITCODE
    $output | Set-Content -LiteralPath (Join-Path $runRoot ($name + '.log')) -Encoding utf8
    if ($exitCode -eq 0) { throw "$name 的独立工具结果已变化；请重新评估 7z 候选，不可沿用旧拒绝结论。" }
    $results.Add([pscustomobject]@{ archive = $name; sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash; accepted = $false; exitCode = $exitCode })
}
[pscustomobject]@{ date = [DateTime]::UtcNow.ToString('O'); tool = $version; results = $results } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'interop-summary.json') -Encoding utf8
Write-Output '本地互操作通过：ZIP/TAR/TAR.GZ 逐项核对成功；两个 7z 候选拒绝证据已保存，产品仍不开放 7z 创建。'
