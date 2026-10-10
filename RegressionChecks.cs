using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

// Compile with the production source and /main:RegressionChecks. Uses only temporary files/registry keys.
internal static class RegressionChecks
{
    private static readonly Type ProgramType = typeof(EdpEDiskAutoRunNative);
    private static object Call(string name, params object[] args)
    {
        return ProgramType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);
    }
    private static void Set(string name, string value)
    {
        ProgramType.GetField(name, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, value);
    }
    private static void Check(bool pass, string message)
    {
        if (!pass) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    private static int Main()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "Utility-regression-" + Guid.NewGuid().ToString("N"));
        string registry = @"Software\EdpEDiskRegression\" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporary);
        try
        {
            Version current = new Version("1.2.17");
            Check((int)Call("SelectInstallAction", null, current, false) == 1, "first install");
            Check((int)Call("SelectInstallAction", new Version("1.2.10.0"), current, false) == 1, "upgrade previous version");
            Check((int)Call("SelectInstallAction", new Version("1.2.17.0"), current, false) == 0, "same version uninstalls (3/4 part version)");
            Check((int)Call("SelectInstallAction", new Version("1.2.18"), current, true) == -1, "old installer cannot downgrade");
            Check((int)Call("SelectInstallAction", new Version("1.2.16"), current, false) == 1, "upgrade latest released version");
            Check((int)Call("SelectInstallAction", new Version("1.2.17.0"), current, true) == 1, "explicit repair");
            Check((int)Call("SelectInstallAction", new Version("1.2.9"), new Version("1.2.10"), false) == 1, "numeric version ordering");
            Check((bool)Call("IsMissingTask", new FileNotFoundException()), "mapped missing task is normal uninstalled state");
            Check((bool)Call("IsMissingTask", new System.Runtime.InteropServices.COMException("missing", unchecked((int)0x8004130F))), "native missing task is normal uninstalled state");
            Check(!(bool)Call("IsMissingTask", new UnauthorizedAccessException()), "access denied is not silently ignored");

            Set("InstallDir", temporary);
            string installed = Path.Combine(temporary, "EdpEDiskAutoRun-1.2.17.exe");
            Set("InstalledExe", installed);
            string watcher = Path.Combine(temporary, "EdpEDiskAutoRun-Watcher-1.2.17.exe");
            Set("InstalledWatcher", watcher);
            Set("StartupRunPath", registry);
            Call("CreateStartupRunValue");
            Check((string)Call("GetStartupExecutable") == watcher, "native logon command points to current watcher");
            string legacyRun = Path.Combine(temporary, "EdpEDiskAutoRun-1.2.16.exe");
            using (RegistryKey startup = Registry.CurrentUser.OpenSubKey(registry, true))
                startup.SetValue("EdpEDiskAutoRun", "\"" + legacyRun + "\" --watch");
            Check((string)Call("GetStartupExecutable") == legacyRun, "legacy registry installation recognized");
            Check(Call("ParseStartupExecutable", "\"C:\\Other\\Other.exe\" --watch") == null, "unrelated startup path rejected");
            Check(Call("ParseStartupExecutable", "\"C:\\bad<path\\EdpEDiskAutoRun.exe\" --watch") == null, "malformed startup path rejected without error");
            Call("RemoveStartupRunValue");
            Check(Call("GetStartupExecutable") == null, "uninstall removes registry startup value");
            Check((bool)Call("IsManagedExecutable", watcher), "native watcher recognized as managed executable");
            string old = Path.Combine(temporary, "EdpEDiskAutoRun-1.2.10.exe");
            string other = Path.Combine(temporary, "EdpEDiskAutoRun-user-data.exe");
            File.WriteAllText(old, "old test build");
            File.WriteAllText(installed, "current test build");
            File.WriteAllText(watcher, "current native build");
            File.WriteAllText(other, "unrelated test file");
            Call("CleanupOldInstalledExecutables", installed, null);
            Check(!File.Exists(old) && File.Exists(installed) && File.Exists(watcher) && File.Exists(other), "upgrade preserves both current components and unrelated file");
            Call("CleanupOldInstalledExecutables", Path.Combine(temporary, "outside.exe"), "");
            Check(!File.Exists(installed) && !File.Exists(watcher) && File.Exists(other), "uninstall removes both components and preserves unrelated file");
            string deploymentSource = Path.Combine(temporary, "source.exe");
            File.WriteAllText(deploymentSource, "first build");
            Call("DeployVersionedExecutable", deploymentSource);
            Check(File.ReadAllText(installed) == "first build", "fresh deployment");
            File.WriteAllText(deploymentSource, "replacement build");
            Call("DeployVersionedExecutable", deploymentSource);
            Check(File.ReadAllText(installed) == "replacement build" && Directory.GetFiles(temporary, "*.new-*").Length == 0, "atomic repair replaces same-version file and removes staging");
            Call("DeployNativeWatcher");
            Check(File.Exists(watcher) && System.Diagnostics.FileVersionInfo.GetVersionInfo(watcher).FileVersion == "1.2.17.0", "native component extracted with matching version");
            Call("DeployNativeWatcher");
            Check(File.Exists(watcher) && Directory.GetFiles(temporary, "*.new-*").Length == 0, "native component atomically repaired without leftover staging");

            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(registry, false);
            Directory.Delete(temporary, true);
        }
    }
}
