# EdpEDiskAutoRun

Windows 下的 EdpEDisk 自动启动与托盘图标固定工具。

> **重要：本工具仅用于外网电脑，内网电脑禁止安装或使用。**

当前版本：`1.2.6`

## 功能

- 插入移动盘后自动查找并启动根目录下的 `EdpEDisk.exe`。
- 将 EdpEDisk 托盘图标保持在任务栏可见区域。
- 开机、EdpEDisk 启动时校正一次，之后每小时校正一次。
- 自动启动监听器安装在当前用户目录，不需要管理员权限。
- 首次安装和版本升级前显示确认窗口，点击取消不会修改系统。
- 弹窗、EXE、任务栏和文件夹图标支持高 DPI、多分辨率显示。

## 使用

1. 确认电脑属于外网环境。**内网电脑请停止操作。**
2. 运行 `EdpEDiskAutoRun.exe`。
3. 阅读红色使用范围警告，点击“确认安装”。
4. 安装后程序会复制到：

   ```text
   %APPDATA%\EdpEDiskAutoRun\EdpEDiskAutoRun-1.2.6.exe
   ```

5. 再次运行相同版本可卸载自动启动功能。

升级时使用带版本号的新文件名，并在切换开机快捷方式后清理旧版本，
不会覆盖仍在运行的旧版 EXE；遇到短暂文件占用会自动等待并重试。

## 安全边界

- 使用 `asInvoker`，仅以当前用户权限运行。
- 不修改 Defender、SmartScreen、组策略或安全例外。
- 不使用 PowerShell/VBS 作为运行时依赖。
- 托盘设置和自动播放设置均写入当前用户范围。
- 发布的 EXE 未使用受信任代码签名证书签名；企业策略可能阻止未签名程序。

## 1.2.6 更新

- 修复升级时覆盖正在运行的旧版 EXE，导致“访问被拒绝”的问题。
- 安装文件改为带版本号的文件名，开机快捷方式以实际目标判断安装版本。
- 停止旧监听器后等待进程退出，超时才终止安装目录内的旧监听器。
- 写入遇到短暂占用时自动重试，并兼容旧文件被设为只读的情况。
- 旧版本文件改为尽力清理，清理失败不再中断新版安装。

## 从源码构建

需要 Windows 自带的 .NET Framework C# 编译器：

```powershell
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

& $csc `
  /nologo `
  /target:winexe `
  /platform:x86 `
  /optimize+ `
  /win32manifest:EdpEDiskAutoRun.manifest `
  /win32icon:EdpEDiskAutoRun.ico `
  /reference:System.Windows.Forms.dll `
  /reference:System.Drawing.dll `
  /reference:Microsoft.CSharp.dll `
  /out:EdpEDiskAutoRun.exe `
  EdpEDiskAutoRunNative.cs
```

## 下载

编译成品和 SHA-256 校验文件仅放在
[GitHub Releases](https://github.com/yangyangha1/EdpEDiskAutoRun/releases)；
代码仓库不存放 EXE、生成预览图或校验输出。

本项目未附带开源许可证；除 GitHub 提供的浏览和派生功能外，作者保留相关权利。
