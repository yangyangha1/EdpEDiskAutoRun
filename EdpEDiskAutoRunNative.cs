using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("EdpEDisk 自动启动与托盘固定")]
[assembly: AssemblyDescription("自动启动移动存储介质中的 EdpEDisk.exe 并保持托盘图标可见")]
[assembly: AssemblyCompany("Local Utility")]
[assembly: AssemblyProduct("EdpEDisk AutoRun")]
[assembly: AssemblyVersion("1.2.17.0")]
[assembly: AssemblyFileVersion("1.2.17.0")]

internal static class EdpEDiskAutoRunNative
{
    private const string ProductName = "EdpEDisk 自动启动与托盘固定";
    private const string ProductVersion = "1.2.17";
    private const string InstallDetails =
        "检测新盘符后会自动寻找根目录下的 EdpEDisk.exe，\r\n" +
        "并在程序启动、图标变化及任务栏重建后保持托盘图标可见。\r\n" +
        "不会自动打开 U 盘文件夹。";
    private static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EdpEDiskAutoRun");
    private static readonly string InstalledExe = Path.Combine(InstallDir, "EdpEDiskAutoRun-" + ProductVersion + ".exe");
    private static readonly string InstalledWatcher = Path.Combine(InstallDir, "EdpEDiskAutoRun-Watcher-" + ProductVersion + ".exe");
    private static readonly string Marker = Path.Combine(InstallDir, "installed.flag");
    private static readonly string State = Path.Combine(InstallDir, "autoplay-state.txt");
    private static readonly string LegacyShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "EdpEDiskAutoRun.lnk");
    private const string ScheduledTaskName = "EdpEDiskAutoRun";
    private static readonly string StartupRunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupRunValueName = "EdpEDiskAutoRun";
    private static readonly string EventName = "Local\\EdpEDiskAutoRun_Stop";
    private static DateTime lastFailureLog = DateTime.MinValue;

    [STAThread]
    private static void Main(string[] args)
    {
        bool watchRequested = HasArgument(args, "--watch");
        bool previewRequested = HasArgument(args, "--preview");
        bool installRequested = HasArgument(args, "--install");
        bool uninstallRequested = HasArgument(args, "--uninstall");

        // The long-running watcher never shows UI. Return through this path
        // before WinForms, visual styles, DPI, fonts, and image resources load.
        if (watchRequested)
        {
            try { Watch(); }
            catch (Exception ex) { LogFailure("watch startup", ex); }
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
            else
            {
                Version installed = GetInstalledVersion();
                int action = SelectInstallAction(installed, new Version(ProductVersion), installRequested);
                if (action == 0) Uninstall();
                else if (action < 0)
                    ShowBrandedDialog("已安装较新版本 " + installed,
                        "当前文件版本为 " + ProductVersion + "，请运行已安装版本进行卸载。", MessageBoxButtons.OK);
                else if (installRequested) Install();
                else if (ShowBrandedDialog(installed == null ? "准备安装 " + ProductVersion + " 版" : "准备覆盖安装 " + ProductVersion + " 版",
                    (installed == null ? "" : "已安装版本：" + installed + "\r\n确认后替换旧版本。\r\n\r\n") + InstallDetails,
                    MessageBoxButtons.YesNo, installed == null ? "确认安装" : "确认更新", "取消") == DialogResult.Yes)
                    Install();
            }
        }
        catch (Exception ex)
        {
            LogFailure("management", ex);
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

    private static int SelectInstallAction(Version installed, Version current, bool forceInstall)
    {
        if (installed == null) return 1;
        int comparison = new Version(current.Major, current.Minor, Math.Max(0, current.Build), Math.Max(0, current.Revision))
            .CompareTo(new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build), Math.Max(0, installed.Revision)));
        if (comparison < 0) return -1;
        return comparison > 0 || forceInstall ? 1 : 0;
    }

    private static Version GetInstalledVersion()
    {
        string startupExe = GetStartupExecutable();
        Version version;
        if (startupExe != null && File.Exists(startupExe) &&
            Version.TryParse(FileVersionInfo.GetVersionInfo(startupExe).FileVersion, out version)) return version;
        string taskExe = GetScheduledExecutable();
        if (!File.Exists(Marker) && taskExe == null && startupExe == null) return null;
        if (IsManagedExecutable(taskExe) && File.Exists(taskExe) &&
            Version.TryParse(FileVersionInfo.GetVersionInfo(taskExe).FileVersion, out version)) return version;
        Version latest = null;
        if (Directory.Exists(InstallDir))
            foreach (string exe in Directory.GetFiles(InstallDir, "EdpEDiskAutoRun*.exe"))
                if (IsManagedExecutable(exe) && Version.TryParse(FileVersionInfo.GetVersionInfo(exe).FileVersion, out version) &&
                    (latest == null || version > latest)) latest = version;
        // A damaged installation still needs an uninstall/repair entry point.
        return latest ?? new Version(ProductVersion);
    }

    private static bool IsManagedExecutable(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), InstallDir, StringComparison.OrdinalIgnoreCase)) return false;
            return IsAutoRunExecutableName(path);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    private static bool IsAutoRunExecutableName(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string name = Path.GetFileNameWithoutExtension(path);
        Version version;
        const string watcherPrefix = "EdpEDiskAutoRun-Watcher-";
        if (name.StartsWith(watcherPrefix, StringComparison.OrdinalIgnoreCase))
            return string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && Version.TryParse(name.Substring(watcherPrefix.Length), out version);
        return string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(name, "EdpEDiskAutoRun", StringComparison.OrdinalIgnoreCase) ||
             (name.StartsWith("EdpEDiskAutoRun-", StringComparison.OrdinalIgnoreCase) && Version.TryParse(name.Substring(16), out version)));
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
        DeployNativeWatcher();
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
        RemoveLegacyScheduledTask();
        CreateStartupRunValue();
        File.WriteAllText(Marker, "installed");
        RemoveLegacyShortcut();
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers"))
            key.SetValue("DisableAutoplay", 1, RegistryValueKind.DWord);
        StartWatcher();
        CleanupOldInstalledExecutables(currentExe);
        ShowBrandedDialog(ProductVersion + " 版安装或更新成功", InstallDetails +
            "\r\n\r\n再次直接运行本 EXE 可卸载自动启动功能；也可使用 --uninstall 参数。",
            MessageBoxButtons.OK);
    }

    private static void Uninstall()
    {
        if (ShowBrandedDialog("检测到已经安装", "是否卸载 EdpEDisk 自动启动功能？",
            MessageBoxButtons.YesNo, "确认卸载", "取消") != DialogResult.Yes) return;
        StopWatcherAndWait();
        RemoveLegacyScheduledTask();
        RemoveStartupRunValue();
        RemoveLegacyShortcut();
        bool hadValue = false;
        int oldValue = 0;
        if (File.Exists(State))
        {
            string[] lines = File.ReadAllLines(State);
            if (lines.Length > 0) hadValue = lines[0] == "1";
            if (lines.Length > 1) int.TryParse(lines[1], out oldValue);
        }
        if (File.Exists(State))
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers"))
            {
                if (hadValue) key.SetValue("DisableAutoplay", oldValue, RegistryValueKind.DWord);
                else key.DeleteValue("DisableAutoplay", false);
            }
        if (File.Exists(Marker)) File.Delete(Marker);
        if (File.Exists(State)) File.Delete(State);
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
            dialog.Text = ProductName + " " + ProductVersion;
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

    private static void StartWatcher() { Watch(); }

    private static void Watch()
    {
        if (!File.Exists(InstalledWatcher)) throw new FileNotFoundException("原生后台文件缺失，请重新安装。", InstalledWatcher);
        using (Process process = Process.Start(new ProcessStartInfo(InstalledWatcher, "--watch") {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })) { }
    }

    private static string GetScheduledExecutable()
    {
        dynamic service = null;
        dynamic root = null;
        dynamic task = null;
        dynamic definition = null;
        dynamic actions = null;
        dynamic action = null;
        try
        {
            service = CreateTaskSchedulerService();
            root = service.GetFolder("\\");
            try { task = root.GetTask(ScheduledTaskName); }
            catch (Exception ex)
            {
                if (IsMissingTask(ex)) return null;
                throw;
            }
            definition = task.Definition;
            actions = definition.Actions;
            action = actions.Item(1);
            return (string)action.Path;
        }
        catch (Exception ex)
        {
            // Legacy scheduler availability must not break registry-based installations.
            LogFailure("legacy task query", ex);
            return null;
        }
        finally
        {
            ReleaseComObject(action);
            ReleaseComObject(actions);
            ReleaseComObject(definition);
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
                if (File.Exists(InstalledExe)) File.Replace(staging, InstalledExe, null);
                else File.Move(staging, InstalledExe);
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

    private static void DeployNativeWatcher()
    {
        string staging = InstalledWatcher + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("EdpEDiskWatcher.exe"))
            {
                if (resource == null) throw new InvalidOperationException("安装文件缺少原生后台组件，请重新下载完整安装文件。");
                using (FileStream file = File.Create(staging)) resource.CopyTo(file);
            }
            ClearReadOnly(InstalledWatcher);
            RetryFileOperation(delegate {
                if (File.Exists(InstalledWatcher)) File.Replace(staging, InstalledWatcher, null);
                else File.Move(staging, InstalledWatcher);
            });
        }
        finally { TryDeleteFile(staging); }
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
                if (!process.ProcessName.StartsWith("EdpEDiskAutoRun", StringComparison.OrdinalIgnoreCase))
                { process.Dispose(); continue; }
                string path = process.MainModule.FileName;
                if (IsManagedExecutable(path))
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
            if (!string.IsNullOrEmpty(keep) &&
                (string.Equals(Path.GetFullPath(path), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetFullPath(path), Path.GetFullPath(InstalledWatcher), StringComparison.OrdinalIgnoreCase))) continue;
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentExe), StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsManagedExecutable(path)) continue;
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

    private static void LogFailure(string operation, Exception error)
    {
        if (DateTime.UtcNow < lastFailureLog.AddMinutes(1)) return;
        lastFailureLog = DateTime.UtcNow;
        try
        {
            Directory.CreateDirectory(InstallDir);
            File.WriteAllText(Path.Combine(InstallDir, "last-error.txt"), DateTime.UtcNow.ToString("o") + " " + operation + "\r\n" + error.ToString());
        }
        catch { }
    }

    private static dynamic CreateTaskSchedulerService()
    {
        Type serviceType = Type.GetTypeFromProgID("Schedule.Service");
        if (serviceType == null) throw new InvalidOperationException("Windows 任务计划程序不可用。");
        dynamic service = Activator.CreateInstance(serviceType);
        service.Connect();
        return service;
    }

    private static string GetStartupExecutable()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupRunPath))
        {
            string command = key == null ? null : key.GetValue(StartupRunValueName) as string;
            return ParseStartupExecutable(command);
        }
    }

    private static string ParseStartupExecutable(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || !command.StartsWith("\"", StringComparison.Ordinal)) return null;
        int end = command.IndexOf('"', 1);
        if (end <= 1) return null;
        string path = command.Substring(1, end - 1);
        return IsManagedExecutable(path) ? path : null;
    }

    private static void CreateStartupRunValue()
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupRunPath))
            key.SetValue(StartupRunValueName, "\"" + InstalledWatcher + "\" --watch", RegistryValueKind.String);
    }

    private static void RemoveStartupRunValue()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupRunPath, true))
            if (key != null) key.DeleteValue(StartupRunValueName, false);
    }

    private static void RemoveLegacyScheduledTask()
    {
        try { DeleteScheduledTask(); }
        catch (Exception ex) { LogFailure("legacy task cleanup", ex); }
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
            catch (Exception ex)
            {
                if (!IsMissingTask(ex)) throw;
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

    private static bool IsMissingTask(Exception error)
    {
        // The CLR maps HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) to
        // FileNotFoundException, not always COMException.
        return error.HResult == unchecked((int)0x80070002) || error.HResult == unchecked((int)0x8004130F);
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint PrivateExtractIcons(
        string fileName, int iconIndex, int iconWidth, int iconHeight,
        IntPtr[] iconHandles, uint[] resourceIds, uint iconCount, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr iconHandle);

}
