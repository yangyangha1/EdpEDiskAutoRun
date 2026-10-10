#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <dbt.h>
#include <wchar.h>
#include <shellapi.h>
#include <objbase.h>

static const wchar_t* TrayPath = L"Control Panel\\NotifyIconSettings";
static ULONGLONG nextDrive = 0, nextProcess = 0, nextTray = 0, nextRefresh = 0;
static DWORD seenDrives = 0, edpPid = 0, explorerPid = 0;
static HWND taskbar = nullptr;
static HANDLE pendingLaunch = nullptr;
static int pendingDrive = -1;
static ULONGLONG retryAt[26] = {};
static DWORD driveGeneration[26] = {}, pendingGeneration = 0;

static bool SupportedDrive(UINT type) {
    return type == DRIVE_REMOVABLE || type == DRIVE_FIXED || type == DRIVE_REMOTE;
}

static bool LaunchPath(const wchar_t* path) {
    return wcslen(path) == 15 && ((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) &&
        _wcsicmp(path + 1, L":\\EdpEDisk.exe") == 0;
}

// Only this short-lived helper loads Shell/COM. UAC is still handled by Windows.
static int LaunchClient(const wchar_t* arguments) {
    wchar_t path[32] = {};
    size_t length = wcslen(arguments);
    if (length != 17 || arguments[0] != L'"' || arguments[length - 1] != L'"') return ERROR_INVALID_PARAMETER;
    wmemcpy(path, arguments + 1, length - 2);
    if (!LaunchPath(path)) return ERROR_INVALID_PARAMETER;
    wchar_t root[] = L"A:\\";
    root[0] = path[0];
    HRESULT initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
    SHELLEXECUTEINFOW execute = {};
    execute.cbSize = sizeof(execute);
    execute.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
    execute.lpFile = path;
    execute.lpDirectory = root;
    execute.nShow = SW_SHOWNORMAL;
    bool launched = ShellExecuteExW(&execute) != FALSE;
    DWORD error = launched ? ERROR_SUCCESS : GetLastError();
    if (!launched && error == ERROR_ELEVATION_REQUIRED) {
        execute.lpVerb = L"runas";
        launched = ShellExecuteExW(&execute) != FALSE;
        error = launched ? ERROR_SUCCESS : GetLastError();
    }
    if (execute.hProcess) CloseHandle(execute.hProcess);
    if (SUCCEEDED(initialized)) CoUninitialize();
    return static_cast<int>(error);
}

static bool StartLaunchHelper(const wchar_t* path, int drive) {
    wchar_t executable[MAX_PATH], command[MAX_PATH + 64];
    DWORD length = GetModuleFileNameW(nullptr, executable, ARRAYSIZE(executable));
    if (!length || length >= ARRAYSIZE(executable)) return false;
    swprintf_s(command, L"\"%s\" --launch \"%s\"", executable, path);
    STARTUPINFOW startup = {};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process = {};
    if (!CreateProcessW(executable, command, nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process)) return false;
    CloseHandle(process.hThread);
    pendingLaunch = process.hProcess;
    pendingDrive = drive;
    pendingGeneration = driveGeneration[drive];
    return true;
}

static DWORD FindProcess(const wchar_t* name) {
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return 0;
    PROCESSENTRY32W entry = {};
    entry.dwSize = sizeof(entry);
    DWORD result = 0;
    if (Process32FirstW(snapshot, &entry)) do {
        if (_wcsicmp(entry.szExeFile, name) == 0) { result = entry.th32ProcessID; break; }
    } while (Process32NextW(snapshot, &entry));
    CloseHandle(snapshot);
    return result;
}

static bool IsEdpRecord(const wchar_t* path, const wchar_t* tooltip) {
    const wchar_t* basename = wcsrchr(path, L'\\');
    basename = basename ? basename + 1 : path;
    return _wcsicmp(basename, L"EdpEDisk.exe") == 0 ||
        wcsstr(tooltip, L"国家电网移动存储介质管理系统") != nullptr;
}

static void ReadString(HKEY key, const wchar_t* name, wchar_t* buffer, DWORD count) {
    DWORD type = 0, size = count * sizeof(wchar_t);
    buffer[0] = 0;
    if (RegQueryValueExW(key, name, nullptr, &type, reinterpret_cast<BYTE*>(buffer), &size) != ERROR_SUCCESS ||
        (type != REG_SZ && type != REG_EXPAND_SZ)) buffer[0] = 0;
    buffer[count - 1] = 0;
    if (size / sizeof(wchar_t) < count) buffer[size / sizeof(wchar_t)] = 0;
}

static bool PromoteIcons(const wchar_t* rootPath = TrayPath) {
    HKEY root = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, rootPath, 0, KEY_READ, &root) != ERROR_SUCCESS) return false;
    bool changed = false;
    wchar_t name[256];
    for (DWORD index = 0; ; ++index) {
        DWORD count = ARRAYSIZE(name);
        LONG status = RegEnumKeyExW(root, index, name, &count, nullptr, nullptr, nullptr, nullptr);
        if (status == ERROR_NO_MORE_ITEMS) break;
        if (status != ERROR_SUCCESS) break;
        HKEY item = nullptr;
        if (RegOpenKeyExW(root, name, 0, KEY_READ, &item) != ERROR_SUCCESS) continue;
        wchar_t path[32768], tooltip[1024];
        ReadString(item, L"ExecutablePath", path, ARRAYSIZE(path));
        ReadString(item, L"InitialTooltip", tooltip, ARRAYSIZE(tooltip));
        DWORD promoted = 0, bytes = sizeof(promoted), type = 0;
        RegQueryValueExW(item, L"IsPromoted", nullptr, &type, reinterpret_cast<BYTE*>(&promoted), &bytes);
        bool needsWrite = IsEdpRecord(path, tooltip) && (type != REG_DWORD || promoted != 1);
        RegCloseKey(item);
        if (needsWrite && RegOpenKeyExW(root, name, 0, KEY_SET_VALUE, &item) == ERROR_SUCCESS) {
            promoted = 1;
            if (RegSetValueExW(item, L"IsPromoted", 0, REG_DWORD, reinterpret_cast<BYTE*>(&promoted), sizeof(promoted)) == ERROR_SUCCESS)
                changed = true;
            RegCloseKey(item);
        }
    }
    RegCloseKey(root);
    return changed;
}

static BOOL CALLBACK RefreshWindow(HWND window, LPARAM target) {
    DWORD pid = 0;
    GetWindowThreadProcessId(window, &pid);
    if (pid == static_cast<DWORD>(target))
        SendNotifyMessageW(window, RegisterWindowMessageW(L"TaskbarCreated"), 0, 0);
    return TRUE;
}

static void RefreshIcon() {
    if (edpPid) EnumWindows(RefreshWindow, static_cast<LPARAM>(edpPid));
}

static void ScanDrives() {
    DWORD drives = GetLogicalDrives(), present = 0;
    for (unsigned int index = 0; index < 26; ++index) {
        DWORD bit = 1u << index;
        if (!(drives & bit)) continue;
        wchar_t root[] = L"A:\\";
        root[0] = static_cast<wchar_t>(L'A' + index);
        if (!SupportedDrive(GetDriveTypeW(root))) continue;
        present |= bit;
        if ((seenDrives & bit) || pendingLaunch || retryAt[index] > GetTickCount64()) continue;
        wchar_t path[] = L"A:\\EdpEDisk.exe";
        path[0] = root[0];
        DWORD attributes = GetFileAttributesW(path);
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
        if (!edpPid) {
            STARTUPINFOW startup = {};
            startup.cb = sizeof(startup);
            startup.dwFlags = STARTF_USESHOWWINDOW;
            startup.wShowWindow = SW_SHOWNORMAL;
            PROCESS_INFORMATION process = {};
            // lpApplicationName is explicit: a path cannot be interpreted as a command.
            if (!CreateProcessW(path, nullptr, nullptr, nullptr, FALSE, 0, nullptr, root, &startup, &process)) {
                DWORD error = GetLastError();
                if (error == ERROR_ELEVATION_REQUIRED && StartLaunchHelper(path, static_cast<int>(index))) continue;
                retryAt[index] = GetTickCount64() + 30000;
                continue;
            }
            edpPid = process.dwProcessId;
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
        }
        seenDrives |= bit;
        retryAt[index] = 0;
        nextTray = GetTickCount64() + 1000;
    }
    seenDrives &= present;
    for (unsigned int index = 0; index < 26; ++index)
        if (!(present & (1u << index))) retryAt[index] = 0;
}

static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wp, LPARAM lp) {
    if (message == WM_DEVICECHANGE && (wp == DBT_DEVICEARRIVAL || wp == DBT_DEVICEREMOVECOMPLETE || wp == DBT_DEVNODES_CHANGED)) {
        if (wp == DBT_DEVICEREMOVECOMPLETE && lp) {
            const DEV_BROADCAST_HDR* header = reinterpret_cast<const DEV_BROADCAST_HDR*>(lp);
            if (header->dbch_devicetype == DBT_DEVTYP_VOLUME && header->dbch_size >= sizeof(DEV_BROADCAST_VOLUME)) {
                DWORD removed = reinterpret_cast<const DEV_BROADCAST_VOLUME*>(lp)->dbcv_unitmask;
                seenDrives &= ~removed;
                for (unsigned int index = 0; index < 26; ++index)
                    if (removed & (1u << index)) { retryAt[index] = 0; ++driveGeneration[index]; }
            }
        }
        nextDrive = GetTickCount64() + 250; // Coalesce the volume's arrival broadcasts.
        return TRUE;
    }
    return DefWindowProcW(window, message, wp, lp);
}

static bool ArmRegistry(HKEY key, HANDLE event) {
    return RegNotifyChangeKeyValue(key, TRUE, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET, event, TRUE) == ERROR_SUCCESS;
}

static int Watch(HINSTANCE instance) {
    HANDLE mutex = CreateMutexW(nullptr, TRUE, L"Local\\EdpEDiskAutoRun_Watcher");
    if (!mutex) return 1;
    if (GetLastError() == ERROR_ALREADY_EXISTS) { CloseHandle(mutex); return 0; }
    HANDLE stop = CreateEventW(nullptr, TRUE, FALSE, L"Local\\EdpEDiskAutoRun_Stop");
    HANDLE changed = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!stop || !changed) {
        if (stop) CloseHandle(stop);
        if (changed) CloseHandle(changed);
        CloseHandle(mutex);
        return 1;
    }
    ResetEvent(stop);
    WNDCLASSW klass = {};
    klass.hInstance = instance;
    klass.lpfnWndProc = WindowProc;
    klass.lpszClassName = L"EdpEDiskAutoRunNativeWatcher";
    RegisterClassW(&klass);
    // A hidden top-level window receives device broadcasts; a message-only window does not.
    HWND window = CreateWindowExW(WS_EX_TOOLWINDOW, klass.lpszClassName, L"", 0, 0, 0, 0, 0, nullptr, nullptr, instance, nullptr);
    if (!window) { CloseHandle(changed); CloseHandle(stop); CloseHandle(mutex); return 1; }
    HKEY tray = nullptr;
    HANDLE waits[] = { stop, changed, nullptr };
    int result = 0;
    while (true) {
        ULONGLONG now = GetTickCount64();
        if (now >= nextProcess) {
            if (!tray && RegOpenKeyExW(HKEY_CURRENT_USER, TrayPath, 0, KEY_READ | KEY_NOTIFY, &tray) == ERROR_SUCCESS) {
                ArmRegistry(tray, changed);
                nextTray = now;
            }
            DWORD pid = FindProcess(L"EdpEDisk.exe"), shellPid = 0;
            HWND shell = FindWindowW(L"Shell_TrayWnd", nullptr);
            GetWindowThreadProcessId(shell, &shellPid);
            if (pid != edpPid || shell != taskbar || shellPid != explorerPid) {
                edpPid = pid;
                taskbar = shell;
                explorerPid = shellPid;
                RefreshIcon();
                nextTray = now;
            }
            nextProcess = now + 5000;
        }
        if (now >= nextDrive) {
            ScanDrives();
            nextDrive = now + 60000;
            for (unsigned int index = 0; index < 26; ++index)
                if (!pendingLaunch && retryAt[index] > now && retryAt[index] < nextDrive) nextDrive = retryAt[index];
        }
        if (now >= nextTray) {
            if (PromoteIcons() && now >= nextRefresh) { RefreshIcon(); nextRefresh = now + 30000; }
            nextTray = now + (edpPid ? 30000 : 60000);
        }
        ULONGLONG due = nextProcess;
        if (nextDrive < due) due = nextDrive;
        if (nextTray < due) due = nextTray;
        now = GetTickCount64();
        DWORD timeout = due > now ? static_cast<DWORD>(due - now) : 0;
        waits[2] = pendingLaunch;
        DWORD signal = MsgWaitForMultipleObjectsEx(pendingLaunch ? 3 : 2, waits, timeout, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        if (signal == WAIT_OBJECT_0) break;
        if (signal == WAIT_OBJECT_0 + 1) {
            if (tray && !ArmRegistry(tray, changed)) { RegCloseKey(tray); tray = nullptr; }
            ULONGLONG soon = GetTickCount64() + 1000;
            if (nextTray > soon) nextTray = soon;
        }
        if (pendingLaunch && signal == WAIT_OBJECT_0 + 2) {
            DWORD exitCode = ERROR_GEN_FAILURE;
            GetExitCodeProcess(pendingLaunch, &exitCode);
            CloseHandle(pendingLaunch);
            pendingLaunch = nullptr;
            // A UAC dialog for a removed volume must not suppress a newly inserted one.
            if (pendingGeneration == driveGeneration[pendingDrive]) {
                if (exitCode == ERROR_SUCCESS || exitCode == ERROR_CANCELLED) seenDrives |= 1u << pendingDrive;
                else retryAt[pendingDrive] = GetTickCount64() + 30000;
            }
            pendingDrive = -1;
            nextProcess = 0;
            nextDrive = GetTickCount64() + 250;
            nextTray = GetTickCount64() + 1000;
        }
        if (signal == WAIT_FAILED) { result = 1; break; }
        MSG message;
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
    }
    if (tray) RegCloseKey(tray);
    if (pendingLaunch) CloseHandle(pendingLaunch);
    DestroyWindow(window);
    CloseHandle(changed);
    CloseHandle(stop);
    ReleaseMutex(mutex);
    CloseHandle(mutex);
    return result;
}

static int SelfTest() {
    wchar_t executable[MAX_PATH], keyPath[256];
    GetModuleFileNameW(nullptr, executable, ARRAYSIZE(executable));
    const wchar_t* basename = wcsrchr(executable, L'\\');
    if (!basename || FindProcess(basename + 1) == 0) return 1;
    if (FindProcess(L"EdpRegressionDefinitelyMissing.exe") != 0) return 2;
    if (!IsEdpRecord(L"E:\\EdpEDisk.exe", L"") || IsEdpRecord(L"E:\\Other.exe", L"other")) return 3;
    if (!SupportedDrive(DRIVE_FIXED) || !SupportedDrive(DRIVE_REMOVABLE) || !SupportedDrive(DRIVE_REMOTE) || SupportedDrive(DRIVE_NO_ROOT_DIR)) return 6;
    if (!LaunchPath(L"E:\\EdpEDisk.exe") || LaunchPath(L"E:\\Other.exe") || LaunchPath(L"E:\\sub\\EdpEDisk.exe")) return 7;
    if (LaunchClient(L"\"E:\\Other.exe\"") != ERROR_INVALID_PARAMETER) return 8;
    DEV_BROADCAST_VOLUME volume = {};
    volume.dbcv_size = sizeof(volume);
    volume.dbcv_devicetype = DBT_DEVTYP_VOLUME;
    volume.dbcv_unitmask = 1u << 4;
    seenDrives = volume.dbcv_unitmask;
    retryAt[4] = GetTickCount64() + 30000;
    WindowProc(nullptr, WM_DEVICECHANGE, DBT_DEVICEREMOVECOMPLETE, reinterpret_cast<LPARAM>(&volume));
    if (seenDrives != 0 || retryAt[4] != 0 || driveGeneration[4] != 1 || nextDrive < GetTickCount64()) return 5;
    swprintf_s(keyPath, L"Software\\EdpEDiskRegression\\native-%lu-%llu", GetCurrentProcessId(), GetTickCount64());
    HKEY root = nullptr, target = nullptr, other = nullptr;
    HANDLE changed = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    int result = 4;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, keyPath, 0, nullptr, 0, KEY_ALL_ACCESS, nullptr, &root, nullptr) == ERROR_SUCCESS &&
        RegCreateKeyExW(root, L"target", 0, nullptr, 0, KEY_ALL_ACCESS, nullptr, &target, nullptr) == ERROR_SUCCESS &&
        RegCreateKeyExW(root, L"other", 0, nullptr, 0, KEY_ALL_ACCESS, nullptr, &other, nullptr) == ERROR_SUCCESS && changed) {
        const wchar_t targetPath[] = L"E:\\EdpEDisk.exe", otherPath[] = L"E:\\Other.exe";
        DWORD zero = 0, value = 0, type = 0, bytes = sizeof(value);
        RegSetValueExW(target, L"ExecutablePath", 0, REG_SZ, reinterpret_cast<const BYTE*>(targetPath), sizeof(targetPath));
        RegSetValueExW(other, L"ExecutablePath", 0, REG_SZ, reinterpret_cast<const BYTE*>(otherPath), sizeof(otherPath));
        RegSetValueExW(target, L"IsPromoted", 0, REG_DWORD, reinterpret_cast<BYTE*>(&zero), sizeof(zero));
        RegSetValueExW(other, L"IsPromoted", 0, REG_DWORD, reinterpret_cast<BYTE*>(&zero), sizeof(zero));
        if (ArmRegistry(root, changed) && PromoteIcons(keyPath) && WaitForSingleObject(changed, 2000) == WAIT_OBJECT_0) {
            RegQueryValueExW(target, L"IsPromoted", nullptr, &type, reinterpret_cast<BYTE*>(&value), &bytes);
            if (value == 1 && !PromoteIcons(keyPath) && ArmRegistry(root, changed)) {
                RegSetValueExW(target, L"IsPromoted", 0, REG_DWORD, reinterpret_cast<BYTE*>(&zero), sizeof(zero));
                if (WaitForSingleObject(changed, 2000) == WAIT_OBJECT_0 && PromoteIcons(keyPath)) {
                    value = 99; bytes = sizeof(value);
                    RegQueryValueExW(other, L"IsPromoted", nullptr, &type, reinterpret_cast<BYTE*>(&value), &bytes);
                    if (value == 0) result = 0;
                }
            }
        }
    }
    if (other) RegCloseKey(other);
    if (target) RegCloseKey(target);
    if (root) RegCloseKey(root);
    if (changed) CloseHandle(changed);
    RegDeleteTreeW(HKEY_CURRENT_USER, keyPath);
    return result;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR commandLine, int) {
    if (wcsncmp(commandLine, L"--launch ", 9) == 0) return LaunchClient(commandLine + 9);
    if (_wcsicmp(commandLine, L"--self-test") == 0) return SelfTest();
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);
    return Watch(instance);
}
