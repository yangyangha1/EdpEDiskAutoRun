# EdpEDiskAutoRun

Windows 下的 EdpEDisk 自动启动与托盘图标固定工具。

当前版本：`1.2.13`

> 本工具仅用于外网电脑，内网电脑不要使用。

## 功能

- 插入 U 盘后自动查找并启动根目录下的 `EdpEDisk.exe`，兼容部分被 Windows 识别为固定磁盘的 USB 设备。
- 将 EdpEDisk 托盘图标保持在任务栏可见区域。
- 开机、EdpEDisk 启动时校正一次，之后每小时校正一次；监听器全程后台运行。
- 自动启动监听器安装在当前用户目录，不需要管理员权限。
- 首次安装和版本升级前显示确认窗口，点击取消不会修改系统。
- 弹窗、EXE、任务栏和文件夹图标支持高 DPI、多分辨率显示。

## 使用

1. 首次安装或升级时直接运行 `EdpEDiskAutoRun-1.2.13.exe`；未检测到安装时会显示安装确认。
2. 阅读安装说明，点击“确认安装”。
3. 安装后程序会复制到：

   ```text
   %APPDATA%\EdpEDiskAutoRun\EdpEDiskAutoRun-1.2.13.exe
   ```

4. 安装时注册当前用户的隐藏登录任务 `EdpEDiskAutoRun`，不创建 Startup 文件夹快捷方式；任务直接后台运行 `--watch`。
5. 已安装时再次直接运行本 EXE 会显示卸载确认；`--install` 和 `--uninstall` 参数仍可用于显式操作。

升级时使用带版本号的新文件名，并在切换登录任务后清理旧版本，
不会覆盖仍在运行的旧版 EXE；遇到短暂文件占用会自动等待并重试。

## 安全边界

- 使用 `asInvoker`，仅以当前用户权限运行。
- 不修改 Defender、SmartScreen、组策略或安全例外。
- 不使用 PowerShell/VBS 作为运行时依赖。
- 托盘设置和自动播放设置均写入当前用户范围。
- 发布的 EXE 未使用受信任代码签名证书签名；企业策略可能阻止未签名程序。

## 1.2.13 更新

- 修复 `EdpEDisk.exe` 需要提升权限时无法由后台监听器自动拉起的问题；改为通过 Windows Shell 正常启动，让系统显示 UAC/密码窗口。

## 1.2.12 更新

- 修复启动失败后把当前 U 盘误记为已处理，导致插入后不再自动重试的问题。
- 启动 `EdpEDisk.exe` 时改为正常窗口启动，保证需要输入密码时窗口可以显示。

## 1.2.11 更新

- 修复部分 U 盘或移动 SSD 被 Windows 识别为固定磁盘时不会自动启动 `EdpEDisk.exe` 的问题。
- 检测到 `EdpEDisk.exe` 由用户或其它方式启动时，会立即延迟校正托盘图标一次，不再只等每小时校正。
- 后台检测间隔调整为 5 秒，托盘图标仍为启动时和每小时校正。
- 安装说明明确本工具仅用于外网电脑，内网电脑不要使用。

## 1.2.10 更新

- 无参数运行时根据当前安装状态显示安装或卸载确认，不再因已安装而无界面退出。
- 只有任务计划程序调用的 `--watch` 参数保持静默后台运行。

## 1.2.8 更新

- 改用当前用户的隐藏任务计划程序登录任务，不再通过 Startup 文件夹打开 EXE。
- 安装或升级时删除旧的 `EdpEDiskAutoRun.lnk`，避免资源管理器弹出“无法验证发布者”提示。

## 1.2.7 更新

- 修复启动快捷方式参数丢失时回退到安装/卸载确认窗口的问题。
- 自动启动 `EdpEDisk.exe` 改为直接后台创建进程，避免 ShellExecute 弹出打开/安全提示。
- 不再强制把 EdpEDisk 窗口置前；启动和监听保持静默。

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
  /out:EdpEDiskAutoRun-1.2.13.exe `
  EdpEDiskAutoRunNative.cs
```

## 下载

编译成品和 SHA-256 校验文件仅放在
[GitHub Releases](https://github.com/yangyangha1/EdpEDiskAutoRun/releases)；
代码仓库不存放 EXE、生成预览图或校验输出。

本项目未附带开源许可证；除 GitHub 提供的浏览和派生功能外，作者保留相关权利。
