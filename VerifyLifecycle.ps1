param([string]$Executable = (Join-Path $PSScriptRoot 'EdpEDiskAutoRun-1.3.0.exe'), [switch]$ResumeReinstall, [switch]$UpgradeFromPrevious, [switch]$RepairInstalled, [switch]$SelfUninstall)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class LifecycleWindow {
    public delegate bool Callback(IntPtr hwnd, IntPtr unused);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, Callback callback, IntPtr unused);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int length);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);
    public static Dictionary<IntPtr,string> Read(IntPtr parent) {
        var result = new Dictionary<IntPtr,string>();
        if (parent == IntPtr.Zero) return result;
        EnumChildWindows(parent, delegate(IntPtr hwnd, IntPtr unused) {
            var text = new StringBuilder(4096);
            GetWindowText(hwnd, text, text.Capacity);
            if (text.Length > 0) result[hwnd] = text.ToString();
            return true;
        }, IntPtr.Zero);
        return result;
    }
    public static void Click(IntPtr hwnd) { PostMessage(hwnd, 0x00F5, IntPtr.Zero, IntPtr.Zero); }
}
'@
function Wait-Dialog($Process, [string]$Heading, [string]$Button) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $Process.Refresh()
        if ($Process.HasExited) { throw "程序在显示 '$Heading' 前退出。" }
        $controls = [LifecycleWindow]::Read($Process.MainWindowHandle)
        if ($controls.Values -contains '操作失败') { throw ($controls.Values -join ' | ') }
        if ($controls.Values -contains $Heading) {
            $match = @($controls.GetEnumerator() | Where-Object {$_.Value -eq $Button})
            if ($match.Count -eq 1) { Write-Output "PASS $Heading"; return $match[0].Key }
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "未找到 '$Heading'；当前控件：$($controls.Values -join ' | ')"
}
function Close-Success($Process, [string]$Heading) {
    $button = Wait-Dialog $Process $Heading '确定'
    [LifecycleWindow]::Click($button[-1])
    if (-not $Process.WaitForExit(10000)) { throw '完成窗口未关闭。' }
}

$install = Join-Path $env:APPDATA 'EdpEDiskAutoRun'
$newTarget = Join-Path $install 'EdpEDiskAutoRun-1.3.0.exe'
$watcherTarget = Join-Path $install 'EdpEDiskAutoRun-Watcher-1.3.0.exe'
$runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
function Get-StartupCommand {
    (Get-ItemProperty -LiteralPath $runPath -Name EdpEDiskAutoRun -ErrorAction SilentlyContinue).EdpEDiskAutoRun
}
function Assert-Installed {
    if ((Get-StartupCommand) -ne ('"' + $watcherTarget + '" --watch')) { throw '登录启动项未直接调用新版原生后台。' }
    if (Get-ScheduledTask -TaskName EdpEDiskAutoRun -ErrorAction SilentlyContinue) { throw '旧计划任务未移除。' }
    if ((Get-FileHash -LiteralPath $newTarget).Hash -ne (Get-FileHash -LiteralPath $Executable).Hash) { throw '安装文件哈希不一致。' }
    if ((Get-FileHash -LiteralPath $watcherTarget).Hash -ne (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'EdpEDiskAutoRun-Watcher-1.3.0.exe')).Hash) { throw '原生后台哈希不一致。' }
}
if ($RepairInstalled) {
    if ($UpgradeFromPrevious -or $ResumeReinstall) { throw '修复测试不能与其它恢复模式同时使用。' }
    $dialog = Start-Process -FilePath $Executable -ArgumentList '--install' -PassThru
    Close-Success $dialog '1.3.0 版安装或更新成功'
    Write-Output 'PASS explicit repair installs freshly rebuilt same version'
}
if ($UpgradeFromPrevious) {
    if ($ResumeReinstall) { throw '不能同时指定两个恢复/升级测试模式。' }
    $previous = Join-Path $PSScriptRoot '.verification-backup\EdpEDiskAutoRun-1.2.17.exe'
    if (-not (Test-Path -LiteralPath $previous)) { throw '没有备份的旧版，无法运行升级测试。' }
    if ((Get-FileHash -LiteralPath $previous).Hash -ne 'DC24859CE1A55D6919FF5B91B37C82C5949631C352697B69D7705484048AA412') { throw '旧版发布文件校验失败。' }
    $dialog = Start-Process -FilePath $previous -ArgumentList '--install' -PassThru
    Close-Success $dialog '1.2.17 版安装或更新成功'
    Write-Output 'PASS prepared latest previous release for upgrade test'
}
if (-not $ResumeReinstall) {
    $oldRun = Get-StartupCommand
    $oldTask = Get-ScheduledTask -TaskName EdpEDiskAutoRun -ErrorAction SilentlyContinue
    if ($oldRun -match '^"([^"]+)"') { $oldTarget = $Matches[1] }
    elseif ($oldTask) { $oldTarget = $oldTask.Actions[0].Execute }
    else { throw '没有现有安装，请使用 ResumeReinstall。' }
    $backup = Join-Path $PSScriptRoot '.verification-backup'
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    Copy-Item -LiteralPath $oldTarget -Destination $backup -Force
    foreach ($name in 'autoplay-state.txt','installed.flag') {
        $file = Join-Path $install $name
        if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination $backup -Force }
    }
    $incomingVersion = [version](Get-Item -LiteralPath $Executable).VersionInfo.FileVersion
    $existingVersion = [version](Get-Item -LiteralPath $oldTarget).VersionInfo.FileVersion
    if ($incomingVersion -lt $existingVersion) { throw '测试文件比已安装版本旧，停止测试，保留现有安装。' }
    if ($incomingVersion -gt $existingVersion) {
        $dialog = Start-Process -FilePath $Executable -PassThru
        $button = Wait-Dialog $dialog '准备覆盖安装 1.3.0 版' '取消'
        [LifecycleWindow]::Click($button[-1])
        if (-not $dialog.WaitForExit(10000)) { throw '取消窗口未退出。' }
        if ((Get-StartupCommand) -ne $oldRun) { throw '取消更新改变了登录启动项。' }
        $afterTask = Get-ScheduledTask -TaskName EdpEDiskAutoRun -ErrorAction SilentlyContinue
        if ($oldTask -and (!$afterTask -or $afterTask.Actions[0].Execute -ne $oldTarget)) { throw '取消更新改变了旧计划任务。' }
        Write-Output 'PASS cancel preserves previous installation'
        $dialog = Start-Process -FilePath $Executable -PassThru
        $button = Wait-Dialog $dialog '准备覆盖安装 1.3.0 版' '确认更新'
        [LifecycleWindow]::Click($button[-1])
        Close-Success $dialog '1.3.0 版安装或更新成功'
        Assert-Installed
        if (Test-Path -LiteralPath $oldTarget) { throw '旧版安装文件未清理。' }
        Write-Output 'PASS upgrade replaces old executable and migrates startup'
    }
    $dialog = Start-Process -FilePath $newTarget -PassThru
    $button = Wait-Dialog $dialog '检测到已经安装' '取消'
    [LifecycleWindow]::Click($button[-1])
    if (-not $dialog.WaitForExit(10000)) { throw '取消卸载未退出。' }
    Assert-Installed
    Write-Output 'PASS installed manager recognizes itself and offers uninstall'
    $uninstaller = if ($SelfUninstall) { $newTarget } else { $Executable }
    $dialog = Start-Process -FilePath $uninstaller -PassThru
    $button = Wait-Dialog $dialog '检测到已经安装' '确认卸载'
    [LifecycleWindow]::Click($button[-1])
    Close-Success $dialog '卸载完成'
    if (Get-ScheduledTask -TaskName EdpEDiskAutoRun -ErrorAction SilentlyContinue) { throw '卸载后任务仍在。' }
    if (Get-StartupCommand) { throw '卸载后登录启动项仍在。' }
    foreach ($name in 'autoplay-state.txt','installed.flag','EdpEDiskAutoRun-1.3.0.exe','EdpEDiskAutoRun-Watcher-1.3.0.exe') {
        if ($SelfUninstall -and $name -eq 'EdpEDiskAutoRun-1.3.0.exe') { continue } # A running EXE cannot delete itself.
        if (Test-Path -LiteralPath (Join-Path $install $name)) { throw "卸载后 $name 仍在。" }
    }
    Write-Output 'PASS uninstall removes both startup modes, executables and restore state'
}
$dialog = Start-Process -FilePath $Executable -PassThru
$button = Wait-Dialog $dialog '准备安装 1.3.0 版' '确认安装'
[LifecycleWindow]::Click($button[-1])
Close-Success $dialog '1.3.0 版安装或更新成功'
Assert-Installed
Write-Output 'PASS reinstall leaves verified new version installed'
