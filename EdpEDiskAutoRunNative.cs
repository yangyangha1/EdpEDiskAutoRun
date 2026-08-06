using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("EdpEDisk 自动启动与托盘固定")]
[assembly: AssemblyDescription("自动启动移动存储介质中的 EdpEDisk.exe 并保持托盘图标可见")]
[assembly: AssemblyCompany("Local Utility")]
[assembly: AssemblyProduct("EdpEDisk AutoRun")]
[assembly: AssemblyVersion("1.2.9.0")]
[assembly: AssemblyFileVersion("1.2.9.0")]

internal static class EdpEDiskAutoRunNative
{
    private const string ProductName = "EdpEDisk 自动启动与托盘固定";
    private const string ProductVersion = "1.2.9";
    private const string InstallDetails =
        "插入 U 盘后会自动寻找根目录下的 EdpEDisk.exe，\r\n" +
        "并在开机、程序启动及每小时定期确认托盘图标保持可见。\r\n" +
        "不会自动打开 U 盘文件夹。";
    private static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EdpEDiskAutoRun");
    private static readonly string InstalledExe = Path.Combine(InstallDir, "EdpEDiskAutoRun-" + ProductVersion + ".exe");
    private static readonly string Marker = Path.Combine(InstallDir, "installed.flag");
    private static readonly string State = Path.Combine(InstallDir, "autoplay-state.txt");
    private static readonly string LegacyShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "EdpEDiskAutoRun.lnk");
    private const string ScheduledTaskName = "EdpEDiskAutoRun";
    private static readonly string EventName = "Local\\EdpEDiskAutoRun_Stop";
    private static readonly string MutexName = "Local\\EdpEDiskAutoRun_Watcher";

    [STAThread]
    private static void Main(string[] args)
    {
        bool watchRequested = HasArgument(args, "--watch");
        bool previewRequested = HasArgument(args, "--preview");
        bool installRequested = HasArgument(args, "--install");
        bool uninstallRequested = HasArgument(args, "--uninstall");

        // The long-running watcher never shows UI. Return through this path
        // before WinForms, visual styles, DPI, fonts, and image resources load.
        // A stale legacy startup entry without arguments must not fall back
        // to the interactive installer either.
        if (watchRequested || (!previewRequested && !installRequested && !uninstallRequested && IsCurrentInstallation()))
        {
            try { Watch(); }
            catch { }
            return;
        }

        EnableHighDpiRendering();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            if (previewRequested)
                ShowBrandedDialog("准备安装或更新 " + ProductVersion + " 版", InstallDetails,
                    MessageBoxButtons.YesNo, "确认安装", "取消");
            else if (uninstallRequested)
                Uninstall();
            else if (ShowBrandedDialog("准备安装或更新 " + ProductVersion + " 版", InstallDetails,
                MessageBoxButtons.YesNo, "确认安装", "取消") == DialogResult.Yes)
                Install();
        }
        catch (Exception ex)
        {
            ShowBrandedDialog("操作失败", ex.Message +
                "\r\n\r\n如果安全软件拦截了本程序，请先确认文件来源可信。",
                MessageBoxButtons.OK);
        }
    }

    private static bool HasArgument(string[] args, string value)
    {
        foreach (string arg in args)
            if (string.Equals(arg, value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsCurrentInstallation()
    {
        return File.Exists(Marker) && InstalledVersionMatchesCurrent() && IsScheduledTaskInstalled();
    }

    private static void EnableHighDpiRendering()
    {
        // Per-Monitor V2 prevents Windows from bitmap-scaling MessageBox text
        // and icons on high-DPI displays. Older Windows versions fall back to
        // the manifest declaration without requiring administrator rights.
        try
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4));
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
    }

    private static void Install()
    {
        Directory.CreateDirectory(InstallDir);
        string currentExe = Process.GetCurrentProcess().MainModule.FileName;
        StopWatcherAndWait();
        if (!string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
            DeployVersionedExecutable(currentExe);
        bool hadValue = false;
        int oldValue = 0;
        if (!File.Exists(State))
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers"))
            {
                if (key != null && key.GetValue("DisableAutoplay") != null)
                {
                    hadValue = true;
                    oldValue = Convert.ToInt32(key.GetValue("DisableAutoplay"));
                }
            }
            File.WriteAllText(State, (hadValue ? "1" : "0") + "\r\n" + oldValue);
        }
        CreateScheduledTask();
        File.WriteAllText(Marker, "installed");
        RemoveLegacyShortcut();
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers"))
            key.SetValue("DisableAutoplay", 1, RegistryValueKind.DWord);
        StartWatcher();
        CleanupOldInstalledExecutables(currentExe);
        ShowBrandedDialog(ProductVersion + " 版安装或更新成功", InstallDetails +
            "\r\n\r\n卸载时请使用本 EXE 的 --uninstall 参数。",
            MessageBoxButtons.OK);
    }

    private static void Uninstall()
    {
        if (ShowBrandedDialog("检测到已经安装", "是否卸载 EdpEDisk 自动启动功能？",
            MessageBoxButtons.YesNo, "确认卸载", "取消") != DialogResult.Yes) return;
        StopWatcherAndWait();
        DeleteScheduledTask();
        RemoveLegacyShortcut();
        bool hadValue = false;
        int oldValue = 0;
        if (File.Exists(State))
        {
            string[] lines = File.ReadAllLines(State);
            if (lines.Length > 0) hadValue = lines[0] == "1";
            if (lines.Length > 1) int.TryParse(lines[1], out oldValue);
        }
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers"))
        {
            if (hadValue) key.SetValue("DisableAutoplay", oldValue, RegistryValueKind.DWord);
            else key.DeleteValue("DisableAutoplay", false);
        }
        if (File.Exists(Marker)) File.Delete(Marker);
        CleanupOldInstalledExecutables(Process.GetCurrentProcess().MainModule.FileName, string.Empty);
        ShowBrandedDialog("卸载完成", "已卸载自动启动功能。", MessageBoxButtons.OK);
    }

    private static DialogResult ShowBrandedDialog(
        string heading,
        string body,
        MessageBoxButtons buttons,
        string yesText = null,
        string noText = null)
    {
        using (Form dialog = new Form())
        using (Image logo = LoadBrandImage())
        {
            dialog.Text = ProductName;
            dialog.Icon = Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName);
            dialog.ShowIcon = true;
            dialog.ShowInTaskbar = true;
            dialog.StartPosition = FormStartPosition.CenterScreen;
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            dialog.MaximizeBox = false;
            dialog.MinimizeBox = false;
            dialog.AutoScaleMode = AutoScaleMode.Dpi;
            dialog.AutoScaleDimensions = new SizeF(96F, 96F);
            dialog.AutoSize = true;
            dialog.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            dialog.MinimumSize = new Size(650, 0);
            dialog.Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.AutoSize = true;
            layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            layout.Padding = new Padding(26, 24, 26, 18);
            layout.ColumnCount = 2;
            layout.RowCount = 3;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            PictureBox picture = new PictureBox();
            picture.Size = new Size(76, 76);
            picture.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            picture.Margin = new Padding(0, 0, 20, 14);
            picture.SizeMode = PictureBoxSizeMode.Zoom;
            picture.Image = logo;

            Label title = new Label();
            title.AutoSize = true;
            title.MaximumSize = new Size(500, 0);
            title.MinimumSize = new Size(500, 0);
            title.Anchor = AnchorStyles.Left;
            title.Margin = new Padding(0, 8, 0, 14);
            title.Text = heading;
            title.TextAlign = ContentAlignment.MiddleLeft;
            title.Font = new Font("Segoe UI", 14F, FontStyle.Bold, GraphicsUnit.Point);

            Label bodyText = new Label();
            bodyText.AutoSize = true;
            bodyText.MaximumSize = new Size(596, 0);
            bodyText.MinimumSize = new Size(596, 0);
            bodyText.Margin = new Padding(0, 0, 0, 18);
            bodyText.Text = body;
            bodyText.TextAlign = ContentAlignment.TopLeft;
            bodyText.Font = new Font("Segoe UI", 10.5F, FontStyle.Regular, GraphicsUnit.Point);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.AutoSize = true;
            actions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            actions.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.WrapContents = false;
            actions.Padding = new Padding(0, 10, 0, 0);

            if (buttons == MessageBoxButtons.YesNo)
            {
                Button no = CreateDialogButton(noText ?? "否(&N)", DialogResult.No);
                Button yes = CreateDialogButton(yesText ?? "是(&Y)", DialogResult.Yes);
                actions.Controls.Add(no);
                actions.Controls.Add(yes);
                dialog.AcceptButton = yes;
                dialog.CancelButton = no;
            }
            else
            {
                Button ok = CreateDialogButton("确定", DialogResult.OK);
                actions.Controls.Add(ok);
                dialog.AcceptButton = ok;
                dialog.CancelButton = ok;
            }

            layout.Controls.Add(picture, 0, 0);
            layout.Controls.Add(title, 1, 0);
            layout.Controls.Add(bodyText, 0, 1);
            layout.SetColumnSpan(bodyText, 2);
            layout.Controls.Add(actions, 0, 2);
            layout.SetColumnSpan(actions, 2);
            dialog.Controls.Add(layout);
            return dialog.ShowDialog();
        }
    }

    private static Button CreateDialogButton(string text, DialogResult result)
    {
        Button button = new Button();
        button.Text = text;
        button.DialogResult = result;
        button.AutoSize = false;
        button.Size = new Size(128, 38);
        button.Margin = new Padding(12, 0, 0, 0);
        button.FlatStyle = FlatStyle.System;
        return button;
    }

    private static Image LoadBrandImage()
    {
        string executable = Assembly.GetExecutingAssembly().Location;
        IntPtr[] handles = new IntPtr[1];
        uint[] resourceIds = new uint[1];
        uint extracted = PrivateExtractIcons(executable, 0, 256, 256, handles, resourceIds, 1, 0);
        if (extracted > 0 && handles[0] != IntPtr.Zero)
        {
            try
            {
                using (Icon source = (Icon)Icon.FromHandle(handles[0]).Clone())
                    return source.ToBitmap();
            }
            finally
            {
                DestroyIcon(handles[0]);
            }
        }

        using (Icon fallback = Icon.ExtractAssociatedIcon(executable))
        {
            if (fallback == null) throw new InvalidOperationException("程序图标资源缺失。");
            return fallback.ToBitmap();
        }
    }

    private static void StartWatcher()
    {
        string exe = InstalledExe;
        Process.Start(new ProcessStartInfo(exe, "--watch") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
    }

    private static void Watch()
    {
        bool createdNew;
        using (Mutex singleInstance = new Mutex(true, MutexName, out createdNew))
        {
            if (!createdNew) return;
            using (EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.ManualReset, EventName))
            {
                string lastVolume = "";
                DateTime nextTrayCorrection = DateTime.MinValue;

                // Explorer can recreate notification-area records late during logon.
                // Waiting briefly prevents Explorer from overwriting our preference.
                if (stop.WaitOne(8000)) return;

                while (true)
                {
                    try
                    {
                        if (DateTime.UtcNow >= nextTrayCorrection)
                        {
                            PromoteAndRefreshTrayIcon();
                            nextTrayCorrection = DateTime.UtcNow.AddHours(1);
                        }
                        DriveInfo[] drives = DriveInfo.GetDrives();
                        foreach (DriveInfo drive in drives)
                        {
                            try
                            {
                                if (drive.DriveType != DriveType.Removable || !drive.IsReady) continue;
                                string exe = Path.Combine(drive.RootDirectory.FullName, "EdpEDisk.exe");
                                if (!File.Exists(exe) || lastVolume == drive.Name) continue;
                                lastVolume = drive.Name;
                                if (Process.GetProcessesByName("EdpEDisk").Length == 0)
                                    Process.Start(new ProcessStartInfo(exe)
                                    {
                                        WorkingDirectory = drive.RootDirectory.FullName,
                                        UseShellExecute = false,
                                        CreateNoWindow = true,
                                        WindowStyle = ProcessWindowStyle.Hidden
                                    });

                                // The icon record is created asynchronously. Wait once,
                                // then apply a single correction for this launch.
                                if (stop.WaitOne(2000)) return;
                                PromoteAndRefreshTrayIcon();
                                nextTrayCorrection = DateTime.UtcNow.AddHours(1);
                            }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }

                        bool stillPresent = false;
                        foreach (DriveInfo drive in drives)
                        {
                            try
                            {
                                if (drive.DriveType == DriveType.Removable && drive.IsReady && drive.Name == lastVolume)
                                    stillPresent = true;
                            }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                        if (!stillPresent) lastVolume = "";
                    }
                    catch
                    {
                        // Removable drives can disappear between enumeration and access.
                        // Keep the watcher alive and retry instead of exiting at logon.
                    }

                    // Drive discovery stays responsive, while tray writes are hourly.
                    if (stop.WaitOne(3000)) return;
                }
            }
        }
    }

    private static bool InstalledVersionMatchesCurrent()
    {
        if (!File.Exists(InstalledExe)) return false;
        string installedVersion = FileVersionInfo.GetVersionInfo(InstalledExe).FileVersion;
        string currentVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();
        return string.Equals(installedVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsScheduledTaskInstalled()
    {
        dynamic service = null;
        dynamic root = null;
        dynamic task = null;
        try
        {
            service = CreateTaskSchedulerService();
            root = service.GetFolder("\\");
            task = root.GetTask(ScheduledTaskName);
            return task != null;
        }
        catch { return false; }
        finally
        {
            ReleaseComObject(task);
            ReleaseComObject(root);
            ReleaseComObject(service);
        }
    }

    private static void DeployVersionedExecutable(string source)
    {
        string staging = InstalledExe + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            RetryFileOperation(delegate { File.Copy(source, staging, true); });
            ClearReadOnly(InstalledExe);
            RetryFileOperation(delegate
            {
                if (File.Exists(InstalledExe)) File.Delete(InstalledExe);
                File.Move(staging, InstalledExe);
            });
        }
        finally
        {
            TryDeleteFile(staging);
        }
    }

    private static void RetryFileOperation(Action operation)
    {
        Exception last = null;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (IOException ex) { last = ex; }
            catch (UnauthorizedAccessException ex) { last = ex; }
            Thread.Sleep(500);
        }
        throw new IOException("安装文件在多次重试后仍无法写入。请关闭正在运行的旧版程序后重试。", last);
    }

    private static void StopWatcherAndWait()
    {
        using (EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.ManualReset, EventName))
            stop.Set();

        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        Process[] remaining;
        do
        {
            remaining = GetManagedProcesses();
            if (remaining.Length == 0) return;
            foreach (Process process in remaining)
            {
                try { process.WaitForExit(250); }
                catch { }
                finally { process.Dispose(); }
            }
        } while (DateTime.UtcNow < deadline);

        foreach (Process process in GetManagedProcesses())
        {
            try
            {
                process.Kill();
                process.WaitForExit(3000);
            }
            catch { }
            finally { process.Dispose(); }
        }
    }

    private static Process[] GetManagedProcesses()
    {
        System.Collections.Generic.List<Process> matches = new System.Collections.Generic.List<Process>();
        int currentProcessId;
        using (Process currentProcess = Process.GetCurrentProcess())
            currentProcessId = currentProcess.Id;
        foreach (Process process in Process.GetProcesses())
        {
            if (process.Id == currentProcessId)
            {
                process.Dispose();
                continue;
            }
            try
            {
                string path = process.MainModule.FileName;
                string directory = Path.GetDirectoryName(path);
                string name = Path.GetFileName(path);
                if (string.Equals(directory, InstallDir, StringComparison.OrdinalIgnoreCase) &&
                    name.StartsWith("EdpEDiskAutoRun", StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(process);
                    continue;
                }
            }
            catch { }
            process.Dispose();
        }
        return matches.ToArray();
    }

    private static void CleanupOldInstalledExecutables(string currentExe, string keepExe = null)
    {
        if (!Directory.Exists(InstallDir)) return;
        string keep = keepExe ?? InstalledExe;
        foreach (string path in Directory.GetFiles(InstallDir, "EdpEDiskAutoRun*.exe"))
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentExe), StringComparison.OrdinalIgnoreCase)) continue;
            TryDeleteFile(path);
        }
    }

    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path)) return;
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static void TryDeleteFile(string path)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return;
                ClearReadOnly(path);
                File.Delete(path);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Thread.Sleep(250);
        }
    }

    private static void RemoveLegacyShortcut()
    {
        if (!File.Exists(LegacyShortcut)) return;
        TryDeleteFile(LegacyShortcut);
        if (File.Exists(LegacyShortcut))
            throw new IOException("无法移除旧的 Startup 开机快捷方式。请关闭占用该快捷方式的程序后重试。");
    }

    private static dynamic CreateTaskSchedulerService()
    {
        Type serviceType = Type.GetTypeFromProgID("Schedule.Service");
        if (serviceType == null) throw new InvalidOperationException("Windows 任务计划程序不可用。");
        dynamic service = Activator.CreateInstance(serviceType);
        service.Connect();
        return service;
    }

    private static void CreateScheduledTask()
    {
        dynamic service = null;
        dynamic root = null;
        dynamic definition = null;
        dynamic trigger = null;
        dynamic action = null;
        try
        {
            service = CreateTaskSchedulerService();
            root = service.GetFolder("\\");
            definition = service.NewTask(0);
            definition.RegistrationInfo.Description = ProductName;
            definition.Principal.LogonType = 3;
            definition.Principal.RunLevel = 0;
            definition.Settings.Enabled = true;
            definition.Settings.Hidden = true;
            definition.Settings.AllowDemandStart = true;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = "PT0S";

            trigger = definition.Triggers.Create(9);
            trigger.UserId = WindowsIdentity.GetCurrent().Name;
            action = definition.Actions.Create(0);
            action.Path = InstalledExe;
            action.Arguments = "--watch";
            action.WorkingDirectory = InstallDir;
            root.RegisterTaskDefinition(ScheduledTaskName, definition, 6, null, null, 3, null);
        }
        finally
        {
            ReleaseComObject(action);
            ReleaseComObject(trigger);
            ReleaseComObject(definition);
            ReleaseComObject(root);
            ReleaseComObject(service);
        }
    }

    private static void DeleteScheduledTask()
    {
        dynamic service = null;
        dynamic root = null;
        try
        {
            service = CreateTaskSchedulerService();
            root = service.GetFolder("\\");
            try
            {
                root.DeleteTask(ScheduledTaskName, 0);
            }
            catch (COMException ex)
            {
                int taskNotFound = unchecked((int)0x8004130F);
                int fileNotFound = unchecked((int)0x80070002);
                if (ex.ErrorCode != taskNotFound && ex.ErrorCode != fileNotFound) throw;
            }
        }
        finally
        {
            ReleaseComObject(root);
            ReleaseComObject(service);
        }
    }

    private static void ReleaseComObject(object value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private static void PromoteAndRefreshTrayIcon()
    {
        if (!PromoteEdpEDiskTrayIcons()) return;
        NotifyEdpEDiskTaskbarCreated();
        Thread.Sleep(300);
        PromoteEdpEDiskTrayIcons();
    }

    private static bool PromoteEdpEDiskTrayIcons()
    {
        // Windows 11 stores each notification icon preference under the
        // current user's NotifyIconSettings key. IsPromoted=1 means the icon
        // stays in the visible tray area. This requires no administrator rights.
        using (RegistryKey root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
        {
            if (root == null) return false;
            bool changed = false;
            foreach (string subKeyName in root.GetSubKeyNames())
            {
                try
                {
                    using (RegistryKey item = root.OpenSubKey(subKeyName, true))
                    {
                        if (item == null) continue;
                        string executablePath = item.GetValue("ExecutablePath") as string;
                        string tooltip = item.GetValue("InitialTooltip") as string;
                        bool isEdpEDisk =
                            (!string.IsNullOrEmpty(executablePath) &&
                             string.Equals(Path.GetFileName(executablePath), "EdpEDisk.exe", StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrEmpty(tooltip) &&
                             tooltip.IndexOf("国家电网移动存储介质管理系统", StringComparison.OrdinalIgnoreCase) >= 0);
                        object promotedValue = item.GetValue("IsPromoted");
                        int promoted = promotedValue == null ? 0 : Convert.ToInt32(promotedValue);
                        if (isEdpEDisk && promoted != 1)
                        {
                            item.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                            changed = true;
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Ignore an inaccessible stale icon record and keep watching.
                }
            }
            return changed;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint PrivateExtractIcons(
        string fileName, int iconIndex, int iconWidth, int iconHeight,
        IntPtr[] iconHandles, uint[] resourceIds, uint iconCount, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr iconHandle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool SendNotifyMessage(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);

    private static void NotifyEdpEDiskTaskbarCreated()
    {
        uint message = RegisterWindowMessage("TaskbarCreated");
        foreach (Process process in Process.GetProcessesByName("EdpEDisk"))
        {
            uint target = (uint)process.Id;
            EnumWindows((hWnd, unused) =>
            {
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid == target)
                    SendNotifyMessage(hWnd, message, UIntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }
    }

}
