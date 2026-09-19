<#[中文说明]
本脚本只执行开发期本地门禁；不包含 CI、Release、插件打包、部署或安装。
每一步检查退出码并保留日志。失败立即停止，不能用后一条成功命令掩盖前面的失败。
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$evidenceDirectory = Join-Path $repoRoot 'artifacts/local-verification'
$packageDirectory = Join-Path $repoRoot 'artifacts/nuget-official'
$studioTests = Join-Path $repoRoot '../myavalonia-workflow-studio/tests/WorkflowStudio.Tests/WorkflowStudio.Tests.csproj'
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
$checks = [Collections.Generic.List[object]]::new()

function Invoke-DotNetCheck([string] $Name, [string[]] $Arguments) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $evidenceDirectory "$Name.log")
    $exitCode = $LASTEXITCODE
    $watch.Stop()
    $checks.Add([pscustomobject]@{ name = $Name; exitCode = $exitCode; seconds = $watch.Elapsed.TotalSeconds; command = 'dotnet ' + ($Arguments -join ' ') })
    if ($exitCode -ne 0) { throw "$Name 未通过，退出码 $exitCode。" }
}

Push-Location -LiteralPath $repoRoot
try {
    # 曾有同版本本地 feed 包占用全局缓存；使用本仓库隔离缓存和官方源，继续严格验证既有锁文件。
    # 此处不重写内容哈希，也不清空其他任务使用的全局缓存。
    Invoke-DotNetCheck 'locked-restore' @('restore', 'LayerUnpackPlugin.slnx', '--locked-mode', '--source', 'https://api.nuget.org/v3/index.json', '--packages', $packageDirectory)
    Invoke-DotNetCheck 'studio-locked-restore' @('restore', $studioTests, '--locked-mode', '--source', 'https://api.nuget.org/v3/index.json', '--packages', $packageDirectory)
    Invoke-DotNetCheck 'debug-build' @('build', 'LayerUnpackPlugin.slnx', '-c', 'Debug', '-warnaserror', '--no-restore')
    # 仅求值 Debug 引用和显式资产，避免项目引用在日后打包时被静默遗漏。
    # ResolveReferences 不执行 DeployManagedPlugin，也不创建正式插件目录或 ZIP。
    $assetArguments = @('msbuild', 'src/LayerUnpackPlugin.Plugin/LayerUnpackPlugin.Plugin.csproj', '-t:ResolveReferences', '-p:Configuration=Debug', '-getItem:ManagedPluginAsset,ManagedPluginPrivatePackage,RuntimeCopyLocalItems')
    $assetWatch = [Diagnostics.Stopwatch]::StartNew()
    $assetExitCode = 1
    try {
        $assetOutput = & dotnet @assetArguments 2>&1
        $nativeExitCode = $LASTEXITCODE
        $assetOutput | Set-Content -LiteralPath (Join-Path $evidenceDirectory 'debug-assets.json') -Encoding utf8
        if ($nativeExitCode -ne 0) { throw "Debug 资产求值失败：$nativeExitCode。" }
        $assets = ($assetOutput -join [Environment]::NewLine) | ConvertFrom-Json
        foreach ($target in @('LayerUnpackPlugin.Headless.dll', 'ThirdPartyNotices/SharpCompress.LICENSE.txt', 'ThirdPartyNotices/SharpZipLib.LICENSE.txt')) {
            $matches = @($assets.Items.ManagedPluginAsset | Where-Object { $_.TargetPath.Replace('\', '/') -eq $target })
            if ($matches.Count -ne 1 -or !(Test-Path -LiteralPath $matches[0].FullPath -PathType Leaf)) {
                throw "Debug 显式资产缺失或重复：$target。"
            }
        }
        foreach ($package in @('SharpCompress', 'SharpZipLib')) {
            $declared = @($assets.Items.ManagedPluginPrivatePackage | Where-Object { $_.Identity -eq $package })
            $runtime = @($assets.Items.RuntimeCopyLocalItems | Where-Object { $_.NuGetPackageId -eq $package })
            if ($declared.Count -ne 1 -or $runtime.Count -ne 1 -or !(Test-Path -LiteralPath $runtime[0].Identity -PathType Leaf)) {
                throw "Debug 私有引擎声明或运行文件缺失：$package。"
            }
        }
        $assetExitCode = 0
        Write-Output 'Debug 引用资产通过：Headless、两个引擎及许可证均有真实文件和显式归属。'
    }
    finally {
        $assetWatch.Stop()
        $checks.Add([pscustomobject]@{ name = 'debug-assets'; exitCode = $assetExitCode; seconds = $assetWatch.Elapsed.TotalSeconds; command = 'dotnet ' + ($assetArguments -join ' ') })
    }
    Invoke-DotNetCheck 'headless-tests' @('test', 'tests/LayerUnpackPlugin.Headless.Tests/LayerUnpackPlugin.Headless.Tests.csproj', '-c', 'Debug', '--no-build', '--logger', 'trx;LogFileName=headless.trx', '--results-directory', $evidenceDirectory)
    Invoke-DotNetCheck 'plugin-tests' @('test', 'tests/LayerUnpackPlugin.Tests/LayerUnpackPlugin.Tests.csproj', '-c', 'Debug', '--no-build', '--logger', 'trx;LogFileName=plugin.trx', '--results-directory', $evidenceDirectory)
    Invoke-DotNetCheck 'workflow-integration-tests' @('test', 'tests/LayerUnpackPlugin.WorkflowIntegration.Tests/LayerUnpackPlugin.WorkflowIntegration.Tests.csproj', '-c', 'Debug', '--no-build', '--logger', 'trx;LogFileName=workflow-integration.trx', '--results-directory', $evidenceDirectory)
    Invoke-DotNetCheck 'studio-debug-build' @('build', $studioTests, '-c', 'Debug', '-warnaserror', '--no-restore')
    Invoke-DotNetCheck 'studio-tests' @('test', $studioTests, '-c', 'Debug', '--no-build', '--logger', 'trx;LogFileName=studio.trx', '--results-directory', $evidenceDirectory)
    $interopWatch = [Diagnostics.Stopwatch]::StartNew()
    $interopExit = 1
    try {
        & (Join-Path $PSScriptRoot 'verify-format-interop.ps1') | Tee-Object -FilePath (Join-Path $evidenceDirectory 'format-interop.log')
        $interopExit = 0
    }
    finally {
        $interopWatch.Stop()
        $checks.Add([pscustomobject]@{ name = 'format-interop'; exitCode = $interopExit; seconds = $interopWatch.Elapsed.TotalSeconds; command = './tools/verify-format-interop.ps1' })
    }
    # VSTest 在发现零测试时可能返回成功，因此结果数量和跳过数同样属于门禁。
    foreach ($file in @('headless.trx', 'plugin.trx', 'workflow-integration.trx', 'studio.trx')) {
        [xml]$trx = Get-Content -LiteralPath (Join-Path $evidenceDirectory $file) -Raw
        $counters = $trx.TestRun.ResultSummary.Counters
        if ([int]$counters.total -eq 0 -or $counters.total -ne $counters.passed -or [int]$counters.notExecuted -ne 0) {
            throw "$file 存在未通过、跳过或零测试结果。"
        }
    }
    Invoke-DotNetCheck 'format' @('format', 'LayerUnpackPlugin.slnx', '--verify-no-changes', '--no-restore')
    $studioRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '../myavalonia-workflow-studio'))
    $studioFiles = @('src/WorkflowStudio.Plugin/Workflows/WorkflowRunner.cs', 'src/WorkflowStudio.Plugin/Workflows/ArchiveWorkflowOutcome.cs', 'tests/WorkflowStudio.Tests/ArchiveWorkflowOutcomeTests.cs') | ForEach-Object { Join-Path $studioRoot $_ }
    Invoke-DotNetCheck 'studio-format' (@('format', $studioTests, '--verify-no-changes', '--no-restore', '--include') + $studioFiles)
    $docsWatch = [Diagnostics.Stopwatch]::StartNew()
    $docsExitCode = 1
    try {
        & (Join-Path $PSScriptRoot 'check-docs.ps1') | Tee-Object -FilePath (Join-Path $evidenceDirectory 'docs.log')
        $docsExitCode = 0
    }
    finally {
        $docsWatch.Stop()
        $checks.Add([pscustomobject]@{ name = 'docs'; exitCode = $docsExitCode; seconds = $docsWatch.Elapsed.TotalSeconds; command = './tools/check-docs.ps1' })
    }
    $diffWatch = [Diagnostics.Stopwatch]::StartNew()
    # 仓库索引保存 LF，Windows 格式化工具可能写 CRLF。仅本命令按 Git 标准归一化换行，
    # 保留对真实行尾空格、Tab 和冲突标记的检查，不更改用户的全局 Git 配置。
    & git -c core.autocrlf=input -c core.safecrlf=false diff --check 2>&1 | Tee-Object -FilePath (Join-Path $evidenceDirectory 'diff-check.log')
    $diffExitCode = $LASTEXITCODE
    $diffWatch.Stop()
    $checks.Add([pscustomobject]@{ name = 'diff-check'; exitCode = $diffExitCode; seconds = $diffWatch.Elapsed.TotalSeconds; command = 'git -c core.autocrlf=input -c core.safecrlf=false diff --check' })
    if ($diffExitCode -ne 0) { throw "差异空白检查未通过，退出码 $diffExitCode。" }
    Write-Output '本地门禁全部通过。发布门禁本轮未执行。'
}
finally {
    $checks | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidenceDirectory 'checks.json') -Encoding utf8
    Pop-Location
}
