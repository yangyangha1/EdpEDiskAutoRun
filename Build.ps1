$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw '需要 Visual Studio C++ Build Tools。' }
    $visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $visualStudio) { throw '没有安装 C++ 编译工具。' }
    $vcvars = Join-Path $visualStudio 'VC\Auxiliary\Build\vcvarsall.bat'
    $buildCommand = 'call "{0}" x86 >nul && rc.exe /nologo /fo Watcher.res Watcher.rc && cl.exe /nologo /utf-8 /W4 /O1 /MT /EHsc /D_WIN32_WINNT=0x0601 Watcher.cpp Watcher.res /Fe:EdpEDiskAutoRun-Watcher-1.2.17.exe /link /SUBSYSTEM:WINDOWS /MANIFEST:NO user32.lib advapi32.lib shell32.lib ole32.lib delayimp.lib /DELAYLOAD:shell32.dll /DELAYLOAD:ole32.dll' -f $vcvars
    & $env:ComSpec /d /s /c $buildCommand
    if ($LASTEXITCODE -ne 0) { throw '原生后台构建失败。' }
    $nativeTest = Start-Process -FilePath (Join-Path $PSScriptRoot 'EdpEDiskAutoRun-Watcher-1.2.17.exe') -ArgumentList '--self-test' -Wait -PassThru
    if ($nativeTest.ExitCode -ne 0) { throw "原生回归检查失败：$($nativeTest.ExitCode)" }
    Write-Output 'PASS native process discovery and tray notification/recovery'
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $csc /nologo /target:winexe /platform:x86 /optimize+ /win32manifest:EdpEDiskAutoRun.manifest /win32icon:EdpEDiskAutoRun.ico /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.CSharp.dll /resource:EdpEDiskAutoRun-Watcher-1.2.17.exe,EdpEDiskWatcher.exe /out:EdpEDiskAutoRun-1.2.17.exe EdpEDiskAutoRunNative.cs
    if ($LASTEXITCODE -ne 0) { throw '安装管理程序构建失败。' }
    & $csc /nologo /target:exe /platform:x86 /main:RegressionChecks /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.CSharp.dll /resource:EdpEDiskAutoRun-Watcher-1.2.17.exe,EdpEDiskWatcher.exe /out:RegressionChecks.exe EdpEDiskAutoRunNative.cs RegressionChecks.cs
    if ($LASTEXITCODE -ne 0) { throw '管理程序回归检查构建失败。' }
    & .\RegressionChecks.exe
    if ($LASTEXITCODE -ne 0) { throw '管理程序回归检查失败。' }
} finally { Pop-Location }
