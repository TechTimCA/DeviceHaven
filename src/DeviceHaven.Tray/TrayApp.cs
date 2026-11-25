using System.Globalization;
using System.Runtime.InteropServices;
using DeviceHaven.Shared;
using static DeviceHaven.Tray.Native;

namespace DeviceHaven.Tray;

/// <summary>
/// The DeviceHaven icon by the clock: shows whether this PC is connected,
/// pops up notifications (messages from IT, installed updates, restarts),
/// and lets the user restart when updates need it.
/// </summary>
internal sealed unsafe class TrayApp
{
    private const string Caption = "DeviceHaven";
    private const uint CallbackMessage = WM_APP + 1;
    private const nuint PollTimer = 1;
    private const int MenuRestart = 10, MenuRecent = 11, MenuAbout = 12;

    private static TrayApp? _instance;

    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly HashSet<Guid> _seen;
    private readonly string _seenPath;
    private nint _hwnd;
    private nint _icon, _largeIcon;
    private bool _iconShown;
    private TrayStatus? _status;
    private DateTimeOffset _lastRestartReminder = DateTimeOffset.MinValue;
    private bool _finalWarningShown;

    public TrayApp()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceHaven");
        Directory.CreateDirectory(dir);
        _seenPath = Path.Combine(dir, "tray-seen.txt");
        _seen = File.Exists(_seenPath)
            ? File.ReadAllLines(_seenPath).Select(l => Guid.TryParse(l, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet()
            : [];
    }

    public void Run(EventWaitHandle showRequested)
    {
        _instance = this;
        CreateWindow();

        var exe = Environment.ProcessPath ?? "DeviceHaven.Tray.exe";
        nint large, small;
        ExtractIconEx(exe, 0, &large, &small, 1);
        _icon = small;
        _largeIcon = large;

        Poll();
        SetTimer(_hwnd, PollTimer, 15_000, 0);

        var handle = showRequested.SafeWaitHandle.DangerousGetHandle();
        MSG msg;
        while (true)
        {
            var wait = MsgWaitForMultipleObjects(1, &handle, false, INFINITE, QS_ALLINPUT);
            if (wait == WAIT_OBJECT_0)
            {
                ShowStatusWindow();   // someone opened DeviceHaven from the Start menu
                continue;
            }
            while (PeekMessage(&msg, 0, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) return;
                TranslateMessage(&msg);
                DispatchMessage(&msg);
            }
        }
    }

    private void CreateWindow()
    {
        var className = "DeviceHavenTrayWindow";
        fixed (char* name = className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = GetModuleHandle(0),
                lpszClassName = name,
            };
            RegisterClassEx(&wc);
        }
        // A hidden top-level window: it receives the icon's clicks and Explorer's restart broadcast.
        _hwnd = CreateWindowEx(0, className, Caption, 0, 0, 0, 0, 0, 0, 0, GetModuleHandle(0), 0);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var app = _instance;
        if (app is not null)
        {
            try
            {
                if (app.Handle(msg, wParam, lParam)) return 0;
            }
            catch (Exception)
            {
                // Never let an error take down the message loop.
            }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private bool Handle(uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_TIMER && wParam == (nint)PollTimer)
        {
            Poll();
            return true;
        }
        if (msg == _taskbarCreated)
        {
            _iconShown = false;   // Explorer restarted: put the icon back
            UpdateIcon();
            return true;
        }
        if (msg == CallbackMessage)
        {
            var evt = (uint)(lParam & 0xFFFF);
            if (evt is WM_CONTEXTMENU or NIN_SELECT or NIN_KEYSELECT or WM_LBUTTONUP) ShowMenu();
            else if (evt == NIN_BALLOONUSERCLICK)
            {
                if (_status?.Restart is { Initiated: false }) AskToRestart();
                else ShowStatusWindow();
            }
            return true;
        }
        return false;
    }

    // ---------- Status ----------

    private void Poll()
    {
        var reply = AgentPipeClient.Send(new TrayRequest(TrayActions.Status));
        _status = reply?.Status;
        UpdateIcon();
        if (_status is null) return;

        ShowNewNotifications(_status);
        RemindAboutRestart(_status);
    }

    private string Tooltip()
    {
        if (_status is null) return "DeviceHaven: agent not running";
        if (!_status.Enrolled) return "DeviceHaven: not set up yet";
        if (_status.Restart is { Initiated: false }) return "DeviceHaven: restart needed";
        return _status.Connected ? "DeviceHaven: connected" : "DeviceHaven: can't reach the server";
    }

    private void UpdateIcon()
    {
        var wanted = _status?.ShowTrayIcon ?? true;
        var data = NewIconData();
        if (!wanted)
        {
            if (_iconShown) Shell_NotifyIcon(NIM_DELETE, &data);
            _iconShown = false;
            return;
        }

        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        Copy(Tooltip(), data.szTip, 128);
        if (!_iconShown)
        {
            _iconShown = Shell_NotifyIcon(NIM_ADD, &data);
            data.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, &data);
        }
        else
        {
            Shell_NotifyIcon(NIM_MODIFY, &data);
        }
    }

    private NOTIFYICONDATAW NewIconData() => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _hwnd,
        uID = 1,
    };

    private void ShowBalloon(string title, string message)
    {
        if (!_iconShown) return;
        var data = NewIconData();
        data.uFlags = NIF_INFO;
        data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON;
        data.hBalloonIcon = _largeIcon;
        Copy(title, data.szInfoTitle, 64);
        Copy(message, data.szInfo, 256);
        Shell_NotifyIcon(NIM_MODIFY, &data);
    }

    // ---------- Notifications ----------

    private void ShowNewNotifications(TrayStatus status)
    {
        var fresh = status.Notifications
            .Where(n => !_seen.Contains(n.Id) && n.CreatedAt > DateTimeOffset.UtcNow.AddDays(-1))
            .ToList();
        if (fresh.Count == 0) return;

        foreach (var n in fresh) _seen.Add(n.Id);
        SaveSeen(status);

        // One pop-up at a time: a restart first, then a message from IT, then the newest of the rest.
        var top = fresh.FirstOrDefault(n => n.Kind == NotificationKinds.Restart)
            ?? fresh.LastOrDefault(n => n.Kind == NotificationKinds.Message)
            ?? fresh[^1];
        var more = fresh.Count > 1 ? $" (+{fresh.Count - 1} more: open DeviceHaven to see them)" : "";
        ShowBalloon(top.Title, top.Message + more);
        if (top.Kind == NotificationKinds.Restart) _lastRestartReminder = DateTimeOffset.UtcNow;
    }

    private void SaveSeen(TrayStatus status)
    {
        // Only remember ids the agent still has, so the file stays small.
        var keep = status.Notifications.Select(n => n.Id).Where(_seen.Contains).Select(g => g.ToString());
        try { File.WriteAllLines(_seenPath, keep); } catch (IOException) { }
    }

    // ---------- Restarts ----------

    private void RemindAboutRestart(TrayStatus status)
    {
        if (status.Restart is not { Initiated: false } restart) return;
        var left = restart.Deadline - DateTimeOffset.UtcNow;

        if (left <= TimeSpan.FromMinutes(15) && !_finalWarningShown)
        {
            _finalWarningShown = true;
            AskToRestart();
            return;
        }
        if (DateTimeOffset.UtcNow - _lastRestartReminder > TimeSpan.FromHours(1))
        {
            _lastRestartReminder = DateTimeOffset.UtcNow;
            ShowBalloon("Restart needed", $"Please restart by {Local(restart.Deadline)}. Click here to restart now.");
        }
    }

    private void AskToRestart()
    {
        var restart = _status?.Restart;
        if (restart is null) return;
        var text = $"{restart.Reason ?? "Your PC needs to restart."}\n\n" +
                   $"Your PC will restart automatically at {Local(restart.Deadline)}.\n\n" +
                   "Save your work, then choose Yes to restart now, or No to restart later.";
        if (MessageBox(_hwnd, text, "DeviceHaven: restart needed", MB_YESNO | MB_ICONWARNING | MB_SETFOREGROUND | MB_TOPMOST) == IDYES)
        {
            var reply = AgentPipeClient.Send(new TrayRequest(TrayActions.RestartNow));
            if (reply is not { Ok: true })
                MessageBox(_hwnd, "The DeviceHaven agent isn't responding, so it couldn't restart this PC. Please restart from the Start menu.",
                    Caption, MB_OK | MB_ICONWARNING | MB_SETFOREGROUND);
        }
    }

    // ---------- Menu and windows ----------

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING | MF_GRAYED, 0, Tooltip().Replace("DeviceHaven: ", "Status: ", StringComparison.Ordinal));
        if (_status?.LastCheckIn is { } last)
            AppendMenu(menu, MF_STRING | MF_GRAYED, 0, $"Last check-in: {Ago(last)}");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        if (_status?.Restart is { Initiated: false }) AppendMenu(menu, MF_STRING, MenuRestart, "Restart now…");
        AppendMenu(menu, MF_STRING, MenuRecent, "Recent notifications");
        AppendMenu(menu, MF_STRING, MenuAbout, "About DeviceHaven");

        POINT p;
        GetCursorPos(&p);
        SetForegroundWindow(_hwnd);
        var choice = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, p.X, p.Y, _hwnd, 0);
        PostMessage(_hwnd, WM_NULL, 0, 0);
        DestroyMenu(menu);

        switch (choice)
        {
            case MenuRestart: AskToRestart(); break;
            case MenuRecent: ShowStatusWindow(); break;
            case MenuAbout:
                MessageBox(_hwnd, $"DeviceHaven Endpoint Management\nEvery Device. One Platform.\n\nTray version {Version()}\nThis PC: {Environment.MachineName}",
                    Caption, MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND);
                break;
        }
    }

    private void ShowStatusWindow()
    {
        Poll();
        var s = _status;
        var text = s is null
            ? "The DeviceHaven agent isn't running on this PC right now."
            : $"Status: {Tooltip()[13..]}\n" +
              $"This PC: {s.DeviceName}\n" +
              $"Server: {s.ServerUrl ?? "not set"}\n" +
              $"Last check-in: {(s.LastCheckIn is { } l ? Ago(l) : "never")}\n" +
              (s.Restart is { Initiated: false } r ? $"Restart needed by {Local(r.Deadline)}\n" : "") +
              "\nRecent notifications:\n" +
              (s.Notifications.Count == 0
                  ? "None"
                  : string.Join("\n", s.Notifications.TakeLast(5).Reverse().Select(n => $"• {Local(n.CreatedAt)}  {n.Title}: {n.Message}")));
        MessageBox(_hwnd, text, Caption, MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND);
    }

    private static string Local(DateTimeOffset t) => t.ToLocalTime().ToString("ddd MMM d, h:mm tt", CultureInfo.CurrentCulture);

    private static string Ago(DateTimeOffset t)
    {
        var mins = (DateTimeOffset.UtcNow - t).TotalMinutes;
        return mins < 1 ? "just now" : mins < 60 ? $"{(int)mins} min ago" : Local(t);
    }

    private static string Version() =>
        typeof(TrayApp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
