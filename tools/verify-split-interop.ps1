<#
开发期独立验证：7zr 只用于核对冻结的合成夹具，不是生产依赖，不生成发布包。
显式指定工具；校验已登记工具摘要后才执行。结果只写入本仓库 artifacts 的全新目录。
#>
param([Parameter(Mandatory = $true)][string] $SevenZipPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureRoot = Join-Path $repoRoot 'tests/LayerUnpackPlugin.Headless.Tests/Fixtures/G0015'
$manifest = Get-Content -LiteralPath (Join-Path $fixtureRoot 'manifest.json') -Raw | ConvertFrom-Json
$tool = (Resolve-Path -LiteralPath $SevenZipPath).Path
if ((Get-FileHash -LiteralPath $tool -Algorithm SHA256).Hash -ne $manifest.toolSha256) { throw '独立工具摘要与夹具登记不符，请核实工具来源后更新证据。' }
$runRoot = Join-Path $repoRoot ('artifacts/G0015/interop-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$results = [Collections.Generic.List[object]]::new()
foreach ($name in @('plain', 'solid', 'content-encrypted', 'header-encrypted', 'performance/load')) {
    $current = $manifest
    $sourceRoot = $fixtureRoot
    if ($name -eq 'performance/load') {
        $sourceRoot = Join-Path $fixtureRoot 'performance'
        $current = Get-Content -LiteralPath (Join-Path $sourceRoot 'manifest.json') -Raw | ConvertFrom-Json
    }
    $baseName = Split-Path $name -Leaf
    $volumes = @($current.volumes | Where-Object { $_.name.StartsWith($baseName + '.7z.') })
    if ($volumes.Count -eq 0) { throw "夹具卷清单为空：$name" }
    foreach ($volume in $volumes) {
        $path = Join-Path $sourceRoot $volume.name
        if ((Get-Item -LiteralPath $path).Length -ne $volume.bytes -or (Get-FileHash -LiteralPath $path).Hash -ne $volume.sha256) { throw "夹具摘要不符：$path" }
    }
    $destination = Join-Path $runRoot $baseName
    New-Item -ItemType Directory -Path $destination | Out-Null
    $log = & $tool x (Join-Path $sourceRoot ($baseName + '.7z.001')) ('-o' + $destination) '-pvolume-test' '-y' 2>&1
    $exitCode = $LASTEXITCODE
    $log | Set-Content -LiteralPath (Join-Path $runRoot ($baseName + '.log')) -Encoding utf8
    if ($exitCode -ne 0) { throw "独立工具解压失败：$name，退出码 $exitCode" }
    foreach ($entry in $current.expected) {
        $path = [IO.Path]::GetFullPath((Join-Path $destination $entry.path))
        if (!$path.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '样本清单路径越界。' }
        if ($entry.directory) {
            if (!(Test-Path -LiteralPath $path -PathType Container)) { throw "目录缺失：$path" }
        } elseif (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $entry.bytes -or
            (Get-FileHash -LiteralPath $path).Hash -ne $entry.sha256) { throw "正文不符：$path" }
    }
    if (@(Get-ChildItem -LiteralPath $destination -Recurse -Force).Count -ne $current.expected.Count) { throw "条目数量不符：$name" }
    $results.Add([pscustomobject]@{ name = $name; volumes = $volumes.Count; entries = $current.expected.Count; passed = $true })
}
[pscustomobject]@{ date = [DateTime]::UtcNow; tool = $manifest.tool; toolSha256 = $manifest.toolSha256; results = $results } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'summary.json') -Encoding utf8
Write-Output "5 组分卷独立互操作通过：$runRoot"
