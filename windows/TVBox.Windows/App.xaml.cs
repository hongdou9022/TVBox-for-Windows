using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TVBoxForWindows.Core;
using TVBoxForWindows.Player;
using TVBoxForWindows.Server;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TVBoxForWindows;

public partial class App : Application
{
    const string AppUserModelId = "TVBox.Windows";
    const string SingleInstanceName = "TVBoxForWindows.SingleInstance";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    static Mutex _singleInstance;

    public static MainWindow Main { get; private set; }
    public static DispatcherQueue Dispatcher { get; private set; }

    public App()
    {
        try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }
        InitializeComponent();
        UnhandledException += (s, e) => { Logger.E("App", e.Exception?.ToString() ?? e.Message); e.Handled = true; };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var instanceName = SingleInstanceName;
        if (Environment.GetEnvironmentVariable("TVBOX_TEST_INSTANCE") == "1") instanceName += ".test";
        _singleInstance = new Mutex(true, instanceName, out var createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            Exit();
            return;
        }

        Dispatcher = DispatcherQueue.GetForCurrentThread();
        AppPaths.Init();
        Setting.Load();
        if (Setting.Migrate()) Stores.NormalizePlaybackSpeed();
        // FFmpeg 引擎启动（缺 dll 时 EngineReady=false，由设置页/播放页给出指引）
        try { PlayerCore.StartEngine(); } catch (Exception e) { Logger.E("App", "StartEngine: " + e.Message); }
        // Stores 预热：后台线程提前加载三张 JSON 表，避免启动后首次浏览卡顿
        _ = Task.Run(() =>
        {
            try { _ = Stores.Configs.Count + Stores.Histories.Count + Stores.Keeps.Count; } catch { }
            AppPaths.CleanupLegacyRuntimes();
        });
        LocalServer.Instance.Start();
        Main = new MainWindow();
        Main.Activate();
        // CloudTV 令牌失效时统一回到登录页（并发请求由 OmniApi 内部防抖）。
        Omni.OmniApi.AuthExpired += () => Post(() => Main?.ShowLogin());
    }

    /// <summary>单实例：把已运行实例的主窗口拉到前台（含托盘隐藏窗口）。</summary>
    static void ActivateExistingInstance()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            var pids = Process.GetProcessesByName(current.ProcessName)
                .Where(process => process.Id != current.Id)
                .Select(process => (uint)process.Id)
                .ToHashSet();
            if (pids.Count == 0) return;

            var visible = IntPtr.Zero;
            var hidden = IntPtr.Zero;
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out var pid);
                if (!pids.Contains(pid) || GetWindowTextLength(hWnd) == 0) return true;
                if (IsWindowVisible(hWnd)) { visible = hWnd; return false; }
                if (hidden == IntPtr.Zero) hidden = hWnd;
                return true;
            }, IntPtr.Zero);

            var handle = visible != IntPtr.Zero ? visible : hidden;
            if (handle == IntPtr.Zero) return;
            ShowWindow(handle, 9);
            SetForegroundWindow(handle);
        }
        catch (Exception e)
        {
            Logger.E("App", "激活已有实例失败: " + e.Message);
        }
    }

    public static void Post(Action action)
    {
        if (Dispatcher == null || Dispatcher.HasThreadAccess) action();
        else Dispatcher.TryEnqueue(() => action());
    }
}
