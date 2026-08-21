using System.Diagnostics;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace EmotionballDeskpet;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\EmotionballDeskpet";

    [STAThread]
    private static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00000000");
        ApplicationConfiguration.Initialize();
        using var singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew || HasLegacyInstanceInCurrentSession())
        {
            return;
        }

        var qaMode = args.Contains("--qa", StringComparer.OrdinalIgnoreCase);
        var url = ResolveBridgeUrl(args);
        try
        {
            Application.Run(new PetForm(url, qaMode));
        }
        finally
        {
            singleInstance.ReleaseMutex();
        }
    }

    private static bool HasLegacyInstanceInCurrentSession()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id == current.Id) continue;
                try
                {
                    if (process.SessionId == current.SessionId) return true;
                }
                catch (InvalidOperationException)
                {
                    // A process that exits during enumeration cannot block startup.
                }
            }
        }
        return false;
    }

    private static string ResolveBridgeUrl(string[] args)
    {
        const string fallback = "http://127.0.0.1:8765/desktop.html";
        var supplied = args.FirstOrDefault(value => Uri.TryCreate(value, UriKind.Absolute, out _));
        if (supplied is null || !Uri.TryCreate(supplied, UriKind.Absolute, out var uri)) return fallback;

        // The desktop host is intentionally restricted to the local bridge.
        // A command-line URL must never turn the WebView into an arbitrary
        // remote page that can send host-control messages.
        var loopback = IPAddress.TryParse(uri.Host, out var address)
            ? IPAddress.IsLoopback(address)
            : string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        return uri.Scheme == Uri.UriSchemeHttp && loopback ? uri.ToString() : fallback;
    }
}

internal sealed class PetForm : Form
{
    private const int ResourceInUseHResult = unchecked((int)0x800700AA);
    private const int WmNclButtonDown = 0x00A1;
    private const int WmMouseMove = 0x0200;
    private const int HtCaption = 0x0002;
    private const int WhMouseLl = 14;
    private const int SwShowNoActivate = 4;
    private const int DefaultWindowSize = 320;
    private const int MinimumWindowSize = 180;
    private const int MaximumWindowSize = 640;
    private const int ResizeStep = 32;
    private const int ScreenMargin = 24;
    private const int HoverTriggerPadding = 18;
    private static readonly TimeSpan InputIdleThreshold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleBubbleVisibleDuration = TimeSpan.FromSeconds(3);

    private readonly string _url;
    private readonly WebView2 _webView;
    private readonly ContextMenuStrip _menu;
    private readonly ContextMenuStrip _trayMenu;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _topMostItem;
    private readonly ToolStripMenuItem _codexBubbleItem;
    private readonly ToolStripMenuItem _appBubbleItem;
    private readonly ToolStripMenuItem _hoverPassThroughItem;
    private readonly ToolStripMenuItem _edgeRelocationItem;
    private readonly Dictionary<string, ToolStripMenuItem> _shapeItems = new();
    private readonly Dictionary<string, ToolStripMenuItem> _bubblePositionItems = new();
    private readonly BridgeProcessManager _bridgeManager;
    private readonly LocalActivityMonitor _localActivityMonitor = new();
    private readonly System.Windows.Forms.Timer _localActivityTimer;
    private readonly System.Windows.Forms.Timer _stateSaveTimer;
    private readonly System.Windows.Forms.Timer _noticeTimer;
    private readonly System.Windows.Forms.Timer _hoverFadeTimer;
    private readonly System.Windows.Forms.Timer _bridgeRecoveryTimer;
    private readonly Random _random = new();
    private readonly LowLevelMouseProc _mouseHookProc;
    private readonly bool _qaMode;
    private readonly string _windowStatePath;
    private PetInputOverlay? _inputOverlay;
    private StatusBubbleForm? _statusBubble;
    private StatusBubbleForm? _noticeBubble;
    private string _selectedShape = "blob";
    private string _selectedBubblePosition = "above";
    private bool _showCodexBubble = true;
    private bool _showAppBubble = true;
    private bool _hoverPassThroughEnabled = true;
    private bool _edgeRelocationEnabled = true;
    private int _hoverVisiblePercent = 45;
    private ToolStripMenuItem? _currentFadeItem;
    private bool _hoverDimmed;
    private bool _hoverEdgeMoveDone;
    private bool _hoverUpdatePending;
    private int _hoverVisualPercent = 100;
    private int _hoverBubbleVisualPercent = 100;
    private int _hoverFadeStartPercent = 100;
    private int _hoverFadeTargetPercent = 100;
    private int _hoverBubbleFadeStartPercent = 100;
    private int _hoverBubbleFadeTargetPercent = 100;
    private DateTime _hoverFadeStartedAt;
    private static readonly TimeSpan HoverFadeDuration = TimeSpan.FromMilliseconds(180);
    private bool _dismissCodexForCurrentTask;
    private bool _leftButtonDown;
    private bool _systemDragActive;
    private bool _clampingToScreen;
    private bool _closing;
    private bool _bridgeRecoveryPending;
    private bool _codexTaskActive;
    private bool _idleBubbleAutoHidden;
    private bool _mouseWakePending;
    private bool _gazeDispatchPending;
    private Point _latestGazeCursorPosition;
    private string _localContextKey = string.Empty;
    private int _localPhraseIndex = -1;
    private DateTime _nextLocalPhraseAt = DateTime.MinValue;
    private DateTime _idleBubbleShownAt = DateTime.MinValue;
    private DateTime _lastObservedMouseMoveAt;
    private Point _lastObservedCursorPosition;
    private Rectangle _dragStartBounds;
    private Point _dragStartCursor;
    private nint _mouseHook;

    private delegate nint LowLevelMouseProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MouseHookData
    {
        public readonly Point Point;
        public readonly uint MouseData;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly nint ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookId, LowLevelMouseProc callback, nint module, uint threadId);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    public PetForm(string url, bool qaMode)
    {
        _url = url;
        _qaMode = qaMode;
        _mouseHookProc = OnLowLevelMouseEvent;
        _lastObservedCursorPosition = Cursor.Position;
        _latestGazeCursorPosition = _lastObservedCursorPosition;
        _lastObservedMouseMoveAt = DateTime.UtcNow;
        AutoScaleMode = AutoScaleMode.None;
        _windowStatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmotionBallCodex",
            qaMode ? "window-state.qa.json" : "window-state.json");
        _bridgeManager = new BridgeProcessManager(FindProjectRoot(), new Uri(url).Port);
        _localActivityTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _localActivityTimer.Tick += (_, _) =>
        {
            UpdateLocalActivity();
            if (_statusBubble is { IsDisposed: false, Visible: true }) SyncStatusBubbleBounds();
            if (_noticeBubble is { IsDisposed: false, Visible: true }) SyncNoticeBubbleBounds();
        };
        _stateSaveTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _stateSaveTimer.Tick += (_, _) =>
        {
            _stateSaveTimer.Stop();
            SaveWindowState();
        };
        _noticeTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _noticeTimer.Tick += (_, _) => EndQuickSettingNotice();
        _hoverFadeTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _hoverFadeTimer.Tick += (_, _) => AdvanceHoverFade();
        _bridgeRecoveryTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _bridgeRecoveryTimer.Tick += async (_, _) => await RecoverBridgeAsync();
        Text = qaMode
            ? "Emotionball-Deskpet · Codex [QA]"
            : "Emotionball-Deskpet · Codex";
        ClientSize = new Size(DefaultWindowSize, DefaultWindowSize);
        MinimumSize = new Size(MinimumWindowSize, MinimumWindowSize);
        MaximumSize = new Size(MaximumWindowSize, MaximumWindowSize);
        FormBorderStyle = qaMode ? FormBorderStyle.Sizable : FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = qaMode;
        TopMost = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        KeyPreview = true;

        RestoreWindowState();

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.Transparent
        };

        _topMostItem = new ToolStripMenuItem("始终置顶")
        {
            Checked = TopMost,
            CheckOnClick = true
        };
        _topMostItem.CheckedChanged += (_, _) =>
        {
            TopMost = _topMostItem.Checked;
            if (_inputOverlay is not null) _inputOverlay.TopMost = TopMost;
            if (_statusBubble is not null) _statusBubble.TopMost = TopMost;
            if (_noticeBubble is not null) _noticeBubble.TopMost = TopMost;
            ShowQuickSettingNotice(_topMostItem.Checked ? "始终置顶" : "取消置顶");
            ScheduleWindowStateSave();
        };

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_topMostItem);
        var sizeMenu = new ToolStripMenuItem("大小");
        sizeMenu.DropDownItems.Add("放大一级", null, (_, _) => ResizeFromCenter(ResizeStep, notice: "放大一级"));
        sizeMenu.DropDownItems.Add("缩小一级", null, (_, _) => ResizeFromCenter(-ResizeStep, notice: "缩小一级"));
        sizeMenu.DropDownItems.Add(new ToolStripSeparator());
        sizeMenu.DropDownItems.Add("小（240）", null, (_, _) => ResizeFromCenter(240, absolute: true, notice: "小"));
        sizeMenu.DropDownItems.Add("中（320）", null, (_, _) => ResizeFromCenter(320, absolute: true, notice: "中"));
        sizeMenu.DropDownItems.Add("大（460）", null, (_, _) => ResizeFromCenter(460, absolute: true, notice: "大"));
        _menu.Items.Add(sizeMenu);
        var shapeMenu = new ToolStripMenuItem("形态");
        AddShapeItem(shapeMenu, "圆胖", "blob");
        AddShapeItem(shapeMenu, "三角", "wedge");
        AddShapeItem(shapeMenu, "菱形", "gem");
        _menu.Items.Add(shapeMenu);
        UpdateShapeMenuChecks();
        var bubbleMenu = new ToolStripMenuItem("气泡");
        var bubblePositionMenu = new ToolStripMenuItem("位置");
        AddBubblePositionItem(bubblePositionMenu, "上方", "above");
        AddBubblePositionItem(bubblePositionMenu, "下方", "below");
        bubbleMenu.DropDownItems.Add(bubblePositionMenu);
        var bubbleVisibilityMenu = new ToolStripMenuItem("显示");
        _codexBubbleItem = new ToolStripMenuItem("Codex 气泡")
        {
            Checked = _showCodexBubble,
            CheckOnClick = true
        };
        _codexBubbleItem.CheckedChanged += (_, _) =>
        {
            _showCodexBubble = _codexBubbleItem.Checked;
            ApplyStatusBubbleVisibility();
            ShowQuickSettingNotice(_codexBubbleItem.Checked ? "Codex气泡开" : "Codex气泡关");
            ScheduleWindowStateSave();
        };
        _appBubbleItem = new ToolStripMenuItem("App 气泡")
        {
            Checked = _showAppBubble,
            CheckOnClick = true
        };
        _appBubbleItem.CheckedChanged += (_, _) =>
        {
            _showAppBubble = _appBubbleItem.Checked;
            ApplyStatusBubbleVisibility();
            ShowQuickSettingNotice(_appBubbleItem.Checked ? "App气泡开" : "App气泡关");
            ScheduleWindowStateSave();
        };
        bubbleVisibilityMenu.DropDownItems.Add(_codexBubbleItem);
        bubbleVisibilityMenu.DropDownItems.Add(_appBubbleItem);
        bubbleMenu.DropDownItems.Add(bubbleVisibilityMenu);
        _menu.Items.Add(bubbleMenu);
        _menu.Items.Add(CreateHoverOpacityMenu());
        UpdateBubblePositionMenuChecks();
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("重新连接", null, (_, _) => _webView.Reload());
        _menu.Items.Add("退出桌宠", null, (_, _) => Close());

        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add(new ToolStripMenuItem("Emotionball-Deskpet 服务运行中") { Enabled = false });
        _trayMenu.Items.Add(new ToolStripSeparator());
        _hoverPassThroughItem = new ToolStripMenuItem("悬停淡化并穿透")
        {
            Checked = _hoverPassThroughEnabled,
            CheckOnClick = true,
            ToolTipText = "鼠标移到桌宠或气泡上时淡化，并把点击交给下面的窗口"
        };
        _hoverPassThroughItem.CheckedChanged += (_, _) =>
        {
            _hoverPassThroughEnabled = _hoverPassThroughItem.Checked;
            ApplyHoverState(Cursor.Position);
            ShowQuickSettingNotice(_hoverPassThroughEnabled ? "悬停穿透开" : "悬停穿透关");
            ScheduleWindowStateSave();
        };
        _trayMenu.Items.Add(_hoverPassThroughItem);
        _edgeRelocationItem = new ToolStripMenuItem("淡化后随机换位")
        {
            Checked = _edgeRelocationEnabled,
            CheckOnClick = true,
            ToolTipText = "气泡渐隐完成后，把桌宠移到屏幕边缘的安全位置"
        };
        _edgeRelocationItem.CheckedChanged += (_, _) =>
        {
            _edgeRelocationEnabled = _edgeRelocationItem.Checked;
            ShowQuickSettingNotice(_edgeRelocationEnabled ? "随机换位开" : "随机换位关");
            ScheduleWindowStateSave();
        };
        _trayMenu.Items.Add(_edgeRelocationItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("重新连接", null, (_, _) => _webView.Reload());
        _trayMenu.Items.Add("退出桌宠", null, (_, _) => Close());
        _trayIcon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = "Emotionball-Deskpet · 后台服务运行中",
            ContextMenuStrip = _trayMenu,
            Visible = false
        };
        _trayIcon.DoubleClick += (_, _) =>
        {
            ShowWindow(Handle, SwShowNoActivate);
            ClampPetToScreen(Cursor.Position);
        };

        Controls.Add(_webView);
        Shown += async (_, _) =>
        {
            // Some launchers pass SW_HIDE even for GUI executables. The bubble
            // and input overlay are separate windows, so that would leave a
            // misleading "bubble only" desktop pet. Always restore the main
            // transparent WebView window without stealing keyboard focus.
            ShowWindow(Handle, SwShowNoActivate);
            InstallMouseHook();
            EnsureInputOverlay();
            await InitializeWebViewAsync();
            if (_closing || IsDisposed) return;
            _localActivityTimer.Start();
            _bridgeRecoveryTimer.Start();
            UpdateLocalActivity(force: true);
        };
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Escape) Close();
        };
        Move += (_, _) =>
        {
            if (_leftButtonDown || _systemDragActive) ClampPetToScreen(Cursor.Position);
            SyncInputOverlayBounds();
            ScheduleWindowStateSave();
        };
        Resize += (_, _) =>
        {
            SyncInputOverlayBounds();
            if (_webView.CoreWebView2 is not null) ApplyWebViewDpiScale();
            ScheduleWindowStateSave();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            var parameters = base.CreateParams;
            if (!_qaMode) parameters.ExStyle |= wsExToolWindow;
            return parameters;
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            await _bridgeManager.EnsureRunningAsync();
            _trayIcon.Visible = true;
            var webViewDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EmotionBallCodex",
                "WebView2");
            Directory.CreateDirectory(webViewDataDirectory);
            var webViewEnvironment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: webViewDataDirectory);
            try
            {
                await _webView.EnsureCoreWebView2Async(webViewEnvironment);
            }
            catch (COMException error) when (error.HResult == ResourceInUseHResult)
            {
                await Task.Delay(750);
                await _webView.EnsureCoreWebView2Async(webViewEnvironment);
            }
            ApplyWebViewDpiScale();
            _webView.DefaultBackgroundColor = Color.Transparent;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.ProcessFailed += (_, _) => TryBeginInvoke(_webView.Reload);
            _webView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                SendHostLayout();
                SendShapeToPage();
                SendHoverOpacityToPage();
            };
            _webView.Source = new Uri(_url);
        }
        catch (Exception error)
        {
            var detail = error.HResult == ResourceInUseHResult
                ? "WebView2 资源正在被另一个桌宠进程占用。请在任务管理器中结束所有 Emotionball-Deskpet.exe 后重试。"
                : error.Message;
            MessageBox.Show(
                this,
                $"桌宠窗口启动失败：{detail}",
                "Emotionball-Deskpet",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
    }

    private async Task RecoverBridgeAsync()
    {
        if (_closing || _bridgeRecoveryPending) return;
        _bridgeRecoveryPending = true;
        try
        {
            var recovered = await _bridgeManager.EnsureRunningAsync();
            if (recovered && !_closing && _webView.CoreWebView2 is not null)
            {
                TryBeginInvoke(_webView.Reload);
            }
        }
        catch
        {
            // The next timer tick retries. Recovery must not terminate the UI.
        }
        finally
        {
            _bridgeRecoveryPending = false;
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs eventArgs)
    {
        base.OnDpiChanged(eventArgs);
        if (_webView.CoreWebView2 is not null) TryBeginInvoke(ApplyWebViewDpiScale);
    }

    private void ApplyWebViewDpiScale()
    {
        var sizeScale = Math.Min(ClientSize.Width, ClientSize.Height) / (double)DefaultWindowSize;
        _webView.ZoomFactor = 96d / Math.Max(96, DeviceDpi) * sizeScale;
        SendHostLayout();
    }

    private void AddShapeItem(ToolStripMenuItem parent, string label, string shape)
    {
        var item = new ToolStripMenuItem(label);
        item.Click += (_, _) => SetShape(shape);
        _shapeItems[shape] = item;
        parent.DropDownItems.Add(item);
    }

    private ToolStripMenuItem CreateHoverOpacityMenu()
    {
        var menu = new ToolStripMenuItem("淡化");
        _currentFadeItem = new ToolStripMenuItem();
        _currentFadeItem.Click += (_, _) =>
        {
            var fadePercent = PromptForFadePercent(100 - _hoverVisiblePercent, "设置当前淡化强度");
            if (fadePercent.HasValue) SetHoverFadePercent(fadePercent.Value);
        };
        menu.DropDownItems.Add(_currentFadeItem);
        UpdateFadeMenuLabels();
        return menu;
    }

    private void SetHoverFadePercent(int fadePercent)
    {
        _hoverVisiblePercent = 100 - Math.Clamp(fadePercent, 0, 100);
        UpdateFadeMenuLabels();
        if (_hoverDimmed)
        {
            StartHoverVisualTransition(true);
        }
        ShowQuickSettingNotice($"淡化{100 - _hoverVisiblePercent}%");
        ScheduleWindowStateSave();
    }

    private void UpdateFadeMenuLabels()
    {
        if (_currentFadeItem is not null)
        {
            _currentFadeItem.Text = $"淡化强度：{100 - _hoverVisiblePercent}%";
        }
    }

    private int? PromptForFadePercent(int initialPercent, string title)
    {
        using var dialog = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(300, 126),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            TopMost = TopMost
        };
        var label = new Label
        {
            AutoSize = true,
            Text = "淡化强度",
            Location = new Point(16, 16)
        };
        var input = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(initialPercent, 0, 100),
            DecimalPlaces = 0,
            Location = new Point(16, 45),
            Width = 110
        };
        var ok = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Location = new Point(158, 78),
            Width = 58
        };
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(224, 78),
            Width = 58
        };
        dialog.Controls.AddRange([label, input, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK ? (int)input.Value : null;
    }

    private void SetShape(string shape)
    {
        _selectedShape = NormalizeShape(shape);
        UpdateShapeMenuChecks();
        SendShapeToPage();
        ShowQuickSettingNotice(_selectedShape switch
        {
            "wedge" => "三角",
            "gem" => "菱形",
            _ => "圆胖"
        });
        ScheduleWindowStateSave();
    }

    private void UpdateShapeMenuChecks()
    {
        foreach (var pair in _shapeItems) pair.Value.Checked = pair.Key == _selectedShape;
    }

    private void SendShapeToPage()
    {
        if (_webView.CoreWebView2 is null || _webView.IsDisposed) return;
        _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "set-shape",
            shape = _selectedShape
        }));
    }

    private void AddBubblePositionItem(ToolStripMenuItem parent, string label, string position)
    {
        var item = new ToolStripMenuItem(label);
        item.Click += (_, _) => SetBubblePosition(position);
        _bubblePositionItems[position] = item;
        parent.DropDownItems.Add(item);
    }

    private void SetBubblePosition(string position)
    {
        _selectedBubblePosition = NormalizeBubblePosition(position);
        UpdateBubblePositionMenuChecks();
        SyncStatusBubbleBounds();
        SyncNoticeBubbleBounds();
        ShowQuickSettingNotice(_selectedBubblePosition == "below" ? "气泡下方" : "气泡上方");
        ScheduleWindowStateSave();
    }

    private void UpdateBubblePositionMenuChecks()
    {
        foreach (var pair in _bubblePositionItems)
        {
            pair.Value.Checked = pair.Key == _selectedBubblePosition;
        }
    }

    private static string NormalizeShape(string? shape) => shape switch
    {
        "wedge" => "wedge",
        "gem" => "gem",
        _ => "blob"
    };

    private static string NormalizeBubblePosition(string? position) =>
        string.Equals(position, "below", StringComparison.OrdinalIgnoreCase) ? "below" : "above";

    private void SendHostLayout()
    {
        if (_webView.CoreWebView2 is null || _webView.IsDisposed) return;
        var payload = JsonSerializer.Serialize(new
        {
            type = "host-layout",
            size = Math.Min(ClientSize.Width, ClientSize.Height)
        });
        _webView.CoreWebView2.PostWebMessageAsJson(payload);
    }

    protected override void OnFormClosed(FormClosedEventArgs eventArgs)
    {
        _closing = true;
        _trayIcon.Visible = false;
        if (_mouseHook != nint.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = nint.Zero;
        }
        _stateSaveTimer.Stop();
        SaveWindowState();
        _localActivityTimer.Stop();
        _noticeTimer.Stop();
        _hoverFadeTimer.Stop();
        _bridgeRecoveryTimer.Stop();
        _localActivityTimer.Dispose();
        _stateSaveTimer.Dispose();
        _noticeTimer.Dispose();
        _hoverFadeTimer.Dispose();
        _bridgeRecoveryTimer.Dispose();
        _inputOverlay?.Close();
        _inputOverlay?.Dispose();
        _statusBubble?.Close();
        _statusBubble?.Dispose();
        _noticeBubble?.Close();
        _noticeBubble?.Dispose();
        _webView.Dispose();
        _menu.Dispose();
        _trayIcon.Dispose();
        _trayMenu.Dispose();
        _bridgeManager.Dispose();
        base.OnFormClosed(eventArgs);
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != nint.Zero) return;
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseHookProc, GetModuleHandle(null), 0);
    }

    private nint OnLowLevelMouseEvent(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var hookData = Marshal.PtrToStructure<MouseHookData>(lParam);
            if (wParam == WmMouseMove)
            {
                QueueGazeUpdate(hookData.Point);
                QueueHoverStateUpdate(hookData.Point);

                if (!_mouseWakePending && !_codexTaskActive
                    && (_localContextKey == "idle" || _idleBubbleAutoHidden)
                    && hookData.Point != _lastObservedCursorPosition)
                {
                    _mouseWakePending = true;
                    if (!TryBeginInvoke(() => WakeFromMouseMovement(hookData.Point)))
                    {
                        _mouseWakePending = false;
                    }
                }
            }
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void QueueHoverStateUpdate(Point cursorPosition)
    {
        if (IsDisposed || Disposing) return;
        _latestGazeCursorPosition = cursorPosition;
        if (_hoverUpdatePending) return;

        _hoverUpdatePending = true;
        if (!TryBeginInvoke(() =>
        {
            _hoverUpdatePending = false;
            ApplyHoverState(_latestGazeCursorPosition);
        }))
        {
            _hoverUpdatePending = false;
        }
    }

    private void ApplyHoverState(Point cursorPosition)
    {
        var shouldDim = _hoverPassThroughEnabled && IsPointOverHoverSurface(cursorPosition);
        if (_hoverDimmed == shouldDim) return;

        _hoverDimmed = shouldDim;
        if (!shouldDim) _hoverEdgeMoveDone = false;
        _inputOverlay?.SetClickThrough(shouldDim);
        _statusBubble?.SetClickThrough(shouldDim);
        _noticeBubble?.SetClickThrough(shouldDim);
        StartHoverVisualTransition(shouldDim);
    }

    private void StartHoverVisualTransition(bool dimmed)
    {
        _hoverFadeStartPercent = _hoverVisualPercent;
        _hoverFadeTargetPercent = dimmed ? _hoverVisiblePercent : 100;
        _hoverBubbleFadeStartPercent = _hoverBubbleVisualPercent;
        _hoverBubbleFadeTargetPercent = dimmed ? 0 : 100;
        if (_hoverFadeStartPercent == _hoverFadeTargetPercent
            && _hoverBubbleFadeStartPercent == _hoverBubbleFadeTargetPercent)
        {
            ApplyHoverVisualPercent(_hoverFadeTargetPercent, _hoverBubbleFadeTargetPercent);
            return;
        }

        _hoverFadeStartedAt = DateTime.UtcNow;
        _hoverFadeTimer.Start();
    }

    private void AdvanceHoverFade()
    {
        var progress = Math.Clamp(
            (DateTime.UtcNow - _hoverFadeStartedAt).TotalMilliseconds / HoverFadeDuration.TotalMilliseconds,
            0d,
            1d);
        var eased = progress * progress * (3d - 2d * progress);
        var nextBody = (int)Math.Round(_hoverFadeStartPercent + (_hoverFadeTargetPercent - _hoverFadeStartPercent) * eased);
        var nextBubble = (int)Math.Round(_hoverBubbleFadeStartPercent + (_hoverBubbleFadeTargetPercent - _hoverBubbleFadeStartPercent) * eased);
        ApplyHoverVisualPercent(nextBody, nextBubble);
        if (progress >= 1d)
        {
            _hoverFadeTimer.Stop();
            ApplyHoverVisualPercent(_hoverFadeTargetPercent, _hoverBubbleFadeTargetPercent);
            if (_hoverDimmed && _edgeRelocationEnabled && !_hoverEdgeMoveDone)
            {
                _hoverEdgeMoveDone = true;
                RandomizePetToScreenEdge();
            }
        }
    }

    private void RandomizePetToScreenEdge()
    {
        var workingArea = Screen.FromRectangle(Bounds).WorkingArea;
        var margin = Math.Max(18, Math.Min(32, Math.Min(Width, Height) / 8));
        var width = Math.Min(Width, workingArea.Width - margin * 2);
        var height = Math.Min(Height, workingArea.Height - margin * 2);
        if (width <= 0 || height <= 0) return;

        var cursorHotZone = new Rectangle(Cursor.Position.X - 160, Cursor.Position.Y - 160, 320, 320);
        var centerHotZone = new Rectangle(
            workingArea.Left + workingArea.Width / 4,
            workingArea.Top + workingArea.Height / 4,
            workingArea.Width / 2,
            workingArea.Height / 2);

        for (var attempt = 0; attempt < 24; attempt++)
        {
            var edge = _random.Next(4);
            var x = workingArea.Left + margin;
            var y = workingArea.Top + margin;
            switch (edge)
            {
                case 0:
                    x = RandomCoordinate(workingArea.Left + margin, workingArea.Right - width - margin);
                    break;
                case 1:
                    x = RandomCoordinate(workingArea.Left + margin, workingArea.Right - width - margin);
                    y = workingArea.Bottom - height - margin;
                    break;
                case 2:
                    y = RandomCoordinate(workingArea.Top + margin, workingArea.Bottom - height - margin);
                    break;
                default:
                    x = workingArea.Right - width - margin;
                    y = RandomCoordinate(workingArea.Top + margin, workingArea.Bottom - height - margin);
                    break;
            }

            var candidate = new Rectangle(x, y, width, height);
            var candidateCenter = new Point(
                candidate.Left + candidate.Width / 2,
                candidate.Top + candidate.Height / 2);
            if (candidate == Bounds
                || cursorHotZone.Contains(candidateCenter)
                || centerHotZone.Contains(candidateCenter)) continue;
            Bounds = candidate;
            ApplyHoverState(Cursor.Position);
            return;
        }

        var fallbackCandidates = new[]
        {
            new Rectangle(workingArea.Left + margin, workingArea.Top + margin, width, height),
            new Rectangle(workingArea.Right - width - margin, workingArea.Top + margin, width, height),
            new Rectangle(workingArea.Left + margin, workingArea.Bottom - height - margin, width, height),
            new Rectangle(workingArea.Right - width - margin, workingArea.Bottom - height - margin, width, height)
        };
        var fallback = fallbackCandidates
            .Select(candidate => ClampBoundsToWorkingArea(candidate, workingArea))
            .FirstOrDefault(candidate => candidate != Bounds);
        if (fallback == Rectangle.Empty) fallback = ClampBoundsToWorkingArea(fallbackCandidates[0], workingArea);
        Bounds = fallback;
        ApplyHoverState(Cursor.Position);
    }

    private int RandomCoordinate(int minimum, int maximum) =>
        maximum <= minimum ? minimum : _random.Next(minimum, maximum + 1);

    private void ApplyHoverVisualPercent(int bodyVisiblePercent, int bubbleVisiblePercent)
    {
        _hoverVisualPercent = Math.Clamp(bodyVisiblePercent, 0, 100);
        _hoverBubbleVisualPercent = Math.Clamp(bubbleVisiblePercent, 0, 100);
        _statusBubble?.SetHoverVisualPercent(_hoverBubbleVisualPercent);
        _noticeBubble?.SetHoverVisualPercent(_hoverBubbleVisualPercent);
        SendHoverOpacityToPage();
    }

    private void SendHoverOpacityToPage()
    {
        if (_webView.CoreWebView2 is not null && !_webView.IsDisposed)
        {
            _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "set-hover-dimmed",
                dimmed = _hoverVisualPercent < 100,
                opacity = _hoverVisualPercent / 100d
            }));
        }
    }

    private bool IsPointOverHoverSurface(Point cursorPosition) =>
        IsWithinHoverTrigger(Bounds, cursorPosition)
        || (_statusBubble is { IsDisposed: false, Visible: true }
            && IsWithinHoverTrigger(_statusBubble.Bounds, cursorPosition))
        || (_noticeBubble is { IsDisposed: false, Visible: true }
            && IsWithinHoverTrigger(_noticeBubble.Bounds, cursorPosition));

    private static bool IsWithinHoverTrigger(Rectangle bounds, Point cursorPosition)
    {
        bounds.Inflate(HoverTriggerPadding, HoverTriggerPadding);
        return bounds.Contains(cursorPosition);
    }

    private void QueueGazeUpdate(Point cursorPosition)
    {
        _latestGazeCursorPosition = cursorPosition;
        if (_gazeDispatchPending || IsDisposed || Disposing) return;
        _gazeDispatchPending = true;
        if (!TryBeginInvoke(() =>
        {
            _gazeDispatchPending = false;
            SendGazeForCursor(_latestGazeCursorPosition);
        }))
        {
            _gazeDispatchPending = false;
        }
    }

    private bool TryBeginInvoke(Action action)
    {
        if (_closing || IsDisposed || Disposing || !IsHandleCreated) return false;
        try
        {
            BeginInvoke(action);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void SendGazeForCursor(Point cursorPosition)
    {
        if (_webView.CoreWebView2 is null || _webView.IsDisposed) return;

        var center = PointToScreen(new Point(ClientSize.Width / 2, ClientSize.Height / 2));
        var dx = cursorPosition.X - center.X;
        var dy = cursorPosition.Y - center.Y;
        // Follow the pointer across the whole virtual desktop. The virtual
        // screen size provides a stable normalization that does not change
        // when the pet is resized or moved to another monitor.
        var virtualScreen = SystemInformation.VirtualScreen;
        var halfWidth = Math.Max(1d, virtualScreen.Width / 2d);
        var halfHeight = Math.Max(1d, virtualScreen.Height / 2d);
        var nx = Math.Clamp(dx / halfWidth, -1d, 1d);
        var ny = Math.Clamp(dy / halfHeight, -1d, 1d);
        _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "set-gaze",
            active = true,
            x = nx,
            y = ny
        }));
    }

    private void WakeFromMouseMovement(Point cursorPosition)
    {
        _mouseWakePending = false;
        _lastObservedCursorPosition = cursorPosition;
        _lastObservedMouseMoveAt = DateTime.UtcNow;
        if (_codexTaskActive || (_localContextKey != "idle" && !_idleBubbleAutoHidden)) return;

        // Do not expose the stale compact idle bubble during wake-up. The
        // foreground app text is rendered first; UpdateLocalActivity then
        // clears this suppression and shows the replacement bubble.
        _idleBubbleAutoHidden = true;
        ApplyStatusBubbleVisibility();
        UpdateLocalActivity(force: true);
    }

    private static string FindProjectRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            var packagedResources = Path.Combine(candidate.FullName, "resources");
            if (File.Exists(Path.Combine(packagedResources, "bridge", "server.mjs")))
            {
                return packagedResources;
            }
            if (File.Exists(Path.Combine(candidate.FullName, "bridge", "server.mjs")))
            {
                return candidate.FullName;
            }
            candidate = candidate.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate bridge/server.mjs above the desktop host directory.");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        if (!IsTrustedWebMessageSource(eventArgs.Source)) return;
        try
        {
            using var message = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            if (message.RootElement.ValueKind != JsonValueKind.Object
                || !message.RootElement.TryGetProperty("type", out var typeValue)
                || typeValue.ValueKind != JsonValueKind.String)
            {
                return;
            }
            var type = typeValue.GetString();
            switch (type)
            {
                case "drag":
                    _systemDragActive = true;
                    SendDragAnimation(active: true);
                    try
                    {
                        ReleaseCapture();
                        SendMessage(Handle, WmNclButtonDown, HtCaption, 0);
                    }
                    finally
                    {
                        _systemDragActive = false;
                        ClampPetToScreen(Cursor.Position);
                        SendDragAnimation(active: false);
                    }
                    break;
                case "resize-step":
                    if (!TryGetInt32(message.RootElement, "delta", out var delta)) return;
                    ResizeFromCenter(
                        delta >= 0 ? ResizeStep : -ResizeStep,
                        notice: delta >= 0 ? "放大一级" : "缩小一级");
                    break;
                case "status":
                    var label = TryGetString(message.RootElement, "label") ?? "未知状态";
                    var online = TryGetBoolean(message.RootElement, "online");
                    var taskName = message.RootElement.TryGetProperty("taskName", out var taskNameValue)
                        && taskNameValue.ValueKind == JsonValueKind.String
                        ? taskNameValue.GetString()
                        : null;
                    var taskActive = online && TryGetBoolean(message.RootElement, "taskActive");
                    var taskJustStarted = !_codexTaskActive && taskActive;
                    _codexTaskActive = taskActive;
                    if (taskJustStarted || !taskActive) _dismissCodexForCurrentTask = false;
                    if (taskActive)
                    {
                        _idleBubbleAutoHidden = false;
                        _idleBubbleShownAt = DateTime.MinValue;
                        _localContextKey = string.Empty;
                        _statusBubble?.UpdateStatus(label, online, taskName, taskActive);
                        SyncStatusBubbleBounds();
                        ApplyStatusBubbleVisibility();
                    }
                    else
                    {
                        UpdateLocalActivity(force: true);
                    }
                    break;
                case "menu":
                    _menu.Show(Cursor.Position);
                    break;
                case "close":
                    Close();
                    break;
                case "toggle-topmost":
                    _topMostItem.Checked = !_topMostItem.Checked;
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed page messages; they never affect the Codex bridge.
        }
        catch (InvalidOperationException)
        {
            // Ignore messages with an unexpected JSON shape.
        }
    }

    private bool IsTrustedWebMessageSource(string? source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var actual)
            || !Uri.TryCreate(_url, UriKind.Absolute, out var expected)) return false;
        return actual.Scheme == expected.Scheme
            && string.Equals(actual.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            && actual.Port == expected.Port;
    }

    private static string? TryGetString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryGetBoolean(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property)
        && property.ValueKind is JsonValueKind.True or JsonValueKind.False
        && property.GetBoolean();

    private static bool TryGetInt32(JsonElement value, string propertyName, out int result)
    {
        result = 0;
        return value.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out result);
    }

    private void UpdateLocalActivity(bool force = false)
    {
        if (_codexTaskActive || _statusBubble is null || _statusBubble.IsDisposed) return;

        var now = DateTime.UtcNow;
        var currentCursorPosition = Cursor.Position;
        if (currentCursorPosition != _lastObservedCursorPosition)
        {
            _lastObservedCursorPosition = currentCursorPosition;
            _lastObservedMouseMoveAt = now;
        }
        var mouseRecentlyMoved = now - _lastObservedMouseMoveAt < InputIdleThreshold;
        if (!mouseRecentlyMoved && _localActivityMonitor.IsInputIdle(InputIdleThreshold))
        {
            if (_localContextKey != "idle")
            {
                _localContextKey = "idle";
                _localPhraseIndex = -1;
                _nextLocalPhraseAt = DateTime.MaxValue;
                _idleBubbleShownAt = now;
                _idleBubbleAutoHidden = false;
                _statusBubble.UpdateStatus("待机放空", online: true, taskName: null, taskActive: false);
                SyncStatusBubbleBounds();
                ApplyStatusBubbleVisibility();
                SendLocalEmotion("04", "待机放空");
            }
            else if (!_idleBubbleAutoHidden
                     && now - _idleBubbleShownAt >= IdleBubbleVisibleDuration)
            {
                _idleBubbleAutoHidden = true;
                ApplyStatusBubbleVisibility();
            }
            return;
        }

        // Keep an auto-hidden idle bubble hidden until its replacement text is
        // fully rendered. Otherwise the old one-line "待机放空" content can be
        // shown again for a frame when input resumes.
        var wakingFromIdle = _localContextKey == "idle" || _idleBubbleAutoHidden;
        _idleBubbleShownAt = DateTime.MinValue;

        var activity = _localActivityMonitor.CaptureForeground();
        var phrases = LocalActivityCatalog.Resolve(activity);
        if (phrases.Count == 0) return;

        var contextChanged = wakingFromIdle
            || !string.Equals(_localContextKey, activity.ContextKey, StringComparison.Ordinal);
        if (!force && !contextChanged && now < _nextLocalPhraseAt) return;

        if (contextChanged || _localPhraseIndex < 0 || _localPhraseIndex >= phrases.Count)
        {
            _localContextKey = activity.ContextKey;
            _localPhraseIndex = _random.Next(phrases.Count);
        }
        else
        {
            _localPhraseIndex = (_localPhraseIndex + 1) % phrases.Count;
        }

        var phrase = phrases[_localPhraseIndex];
        var text = phrase.Text.Replace("{app}", activity.AppName, StringComparison.Ordinal);
        _statusBubble.UpdateLocalActivity(text, phrase.State);
        SyncStatusBubbleBounds();
        _idleBubbleAutoHidden = false;
        ApplyStatusBubbleVisibility();
        SendLocalEmotion(phrase.EmotionId, text);
        _nextLocalPhraseAt = now.AddSeconds(25);
    }

    private void SendLocalEmotion(string emotionId, string tips)
    {
        if (_webView.CoreWebView2 is null || _webView.IsDisposed) return;
        _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "set-local-emotion",
            emotionId,
            tips
        }));
    }

    private void SendDragAnimation(bool active)
    {
        if (_webView.CoreWebView2 is null || _webView.IsDisposed) return;
        _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "set-dragging",
            active
        }));
    }

    private void ApplyStatusBubbleVisibility()
    {
        if (_statusBubble is null || _statusBubble.IsDisposed) return;
        if (_noticeBubble is { IsDisposed: false, Visible: true }) return;

        var show = _codexTaskActive
            ? _showCodexBubble && !_dismissCodexForCurrentTask
            : _showAppBubble && !_idleBubbleAutoHidden;
        _statusBubble.SetTemporaryDismissEnabled(_codexTaskActive && _showCodexBubble);
        if (show)
        {
            SyncStatusBubbleBounds();
            if (!_statusBubble.Visible)
            {
                _statusBubble.Show(this);
                // Showing a layered window can cause Windows to apply its last
                // native bounds. Re-center once more using the new content size.
                SyncStatusBubbleBounds();
                TryBeginInvoke(() =>
                {
                    if (_statusBubble is { IsDisposed: false, Visible: true }) SyncStatusBubbleBounds();
                });
            }
        }
        else
        {
            _statusBubble.Hide();
        }
    }

    private void ShowQuickSettingNotice(string text)
    {
        if (_noticeBubble is null || _noticeBubble.IsDisposed || string.IsNullOrWhiteSpace(text)) return;

        _noticeTimer.Stop();
        _noticeBubble.TopMost = TopMost;
        _noticeBubble.UpdateStatus(text, online: true, taskName: null, taskActive: false);
        _statusBubble?.Hide();
        SyncNoticeBubbleBounds();
        if (!_noticeBubble.Visible) _noticeBubble.Show(this);
        SyncNoticeBubbleBounds();
        _noticeTimer.Start();
    }

    private void EndQuickSettingNotice()
    {
        _noticeTimer.Stop();
        if (_noticeBubble is { IsDisposed: false }) _noticeBubble.Hide();
        ApplyStatusBubbleVisibility();
    }

    internal void OnNativeMouseDown(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Right)
        {
            _menu.Show(Cursor.Position);
            return;
        }
        if (eventArgs.Button != MouseButtons.Left) return;
        _leftButtonDown = true;
        SendDragAnimation(active: true);
        _dragStartCursor = Cursor.Position;
        _dragStartBounds = Bounds;
        if (sender is Control control) control.Capture = true;
    }

    internal void OnNativeMouseMove(object? sender, MouseEventArgs eventArgs)
    {
        if (_leftButtonDown)
        {
            var cursor = Cursor.Position;
            var proposedBounds = new Rectangle(
                _dragStartBounds.Left + cursor.X - _dragStartCursor.X,
                _dragStartBounds.Top + cursor.Y - _dragStartCursor.Y,
                Width,
                Height);
            Bounds = ClampBoundsToWorkingArea(proposedBounds, Screen.FromPoint(cursor).WorkingArea);
            return;
        }

        if (sender is Control control) control.Cursor = Cursors.SizeAll;
    }

    internal void OnNativeMouseUp(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left) return;
        _leftButtonDown = false;
        ClampPetToScreen(Cursor.Position);
        SendDragAnimation(active: false);
        if (sender is Control control) control.Capture = false;
    }

    internal void OnNativeMouseCaptureChanged(object? sender, EventArgs eventArgs)
    {
        if (sender is not Control { Capture: false } || !_leftButtonDown) return;
        _leftButtonDown = false;
        ClampPetToScreen(Cursor.Position);
        SendDragAnimation(active: false);
    }

    internal void OnNativeMouseWheel(object? sender, MouseEventArgs eventArgs)
    {
        if (!_leftButtonDown || eventArgs.Delta == 0) return;
        var growing = eventArgs.Delta > 0;
        ResizeFromCenter(growing ? ResizeStep : -ResizeStep, notice: growing ? "放大一级" : "缩小一级");
        // Resizing around the center changes the origin. Re-anchor the drag so
        // the pet does not jump when the mouse moves again.
        _dragStartCursor = Cursor.Position;
        _dragStartBounds = Bounds;
    }

    internal void OnNativeMouseDoubleClick(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left) _topMostItem.Checked = !_topMostItem.Checked;
    }

    private void ResizeFromCenter(int value, bool absolute = false, string? notice = null)
    {
        var nextSize = Math.Clamp(
            absolute ? value : Math.Max(Width, Height) + value,
            MinimumWindowSize,
            MaximumWindowSize);
        var center = new Point(Left + Width / 2, Top + Height / 2);
        var proposedBounds = new Rectangle(center.X - nextSize / 2, center.Y - nextSize / 2, nextSize, nextSize);
        Bounds = ClampBoundsToWorkingArea(proposedBounds, Screen.FromPoint(center).WorkingArea);
        QueueGazeUpdate(Cursor.Position);
        if (!string.IsNullOrWhiteSpace(notice)) ShowQuickSettingNotice(notice);
    }

    private void ClampPetToScreen(Point referencePoint)
    {
        if (_clampingToScreen) return;
        var clamped = ClampBoundsToWorkingArea(Bounds, Screen.FromPoint(referencePoint).WorkingArea);
        if (clamped == Bounds) return;
        _clampingToScreen = true;
        try
        {
            Bounds = clamped;
        }
        finally
        {
            _clampingToScreen = false;
        }
    }

    private static Rectangle ClampBoundsToWorkingArea(Rectangle bounds, Rectangle workingArea)
    {
        var width = Math.Min(bounds.Width, workingArea.Width);
        var height = Math.Min(bounds.Height, workingArea.Height);
        var x = Math.Clamp(bounds.X, workingArea.Left, workingArea.Right - width);
        var y = Math.Clamp(bounds.Y, workingArea.Top, workingArea.Bottom - height);
        return new Rectangle(x, y, width, height);
    }

    private void RestoreWindowState()
    {
        try
        {
            if (!File.Exists(_windowStatePath))
            {
                PositionAtBottomRight();
                return;
            }

            var state = JsonSerializer.Deserialize<SavedWindowState>(File.ReadAllText(_windowStatePath));
            if (state is null)
            {
                PositionAtBottomRight();
                return;
            }

            var size = Math.Clamp(state.Size, MinimumWindowSize, MaximumWindowSize);
            var restored = new Rectangle(state.X, state.Y, size, size);
            if (!Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(restored)))
            {
                PositionAtBottomRight();
                return;
            }

            Bounds = ClampBoundsToWorkingArea(restored, Screen.FromRectangle(restored).WorkingArea);
            TopMost = state.TopMost;
            _selectedShape = NormalizeShape(state.Shape);
            _selectedBubblePosition = NormalizeBubblePosition(state.BubblePosition);
            var legacyBubbleVisibility = state.ShowBubble ?? true;
            _showCodexBubble = state.ShowCodexBubble ?? legacyBubbleVisibility;
            _showAppBubble = state.ShowAppBubble ?? legacyBubbleVisibility;
            _hoverPassThroughEnabled = state.HoverPassThrough ?? true;
            _edgeRelocationEnabled = state.EdgeRelocation ?? true;
            _hoverVisiblePercent = Math.Clamp(state.HoverVisiblePercent ?? 45, 0, 100);
        }
        catch (IOException)
        {
            PositionAtBottomRight();
        }
        catch (JsonException)
        {
            PositionAtBottomRight();
        }
        catch (UnauthorizedAccessException)
        {
            PositionAtBottomRight();
        }
        catch (SecurityException)
        {
            PositionAtBottomRight();
        }
        catch (NotSupportedException)
        {
            PositionAtBottomRight();
        }
    }

    private void PositionAtBottomRight()
    {
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(this);
        Location = new Point(
            Math.Max(workingArea.Left, workingArea.Right - Width - ScreenMargin),
            Math.Max(workingArea.Top, workingArea.Bottom - Height - ScreenMargin));
    }

    private void SaveWindowState()
    {
        try
        {
            var directory = Path.GetDirectoryName(_windowStatePath)!;
            Directory.CreateDirectory(directory);
            var state = new SavedWindowState(
                Left,
                Top,
                Math.Clamp(Math.Max(Width, Height), MinimumWindowSize, MaximumWindowSize),
                TopMost,
                _selectedShape,
                _selectedBubblePosition,
                null,
                _showCodexBubble,
                _showAppBubble,
                _hoverPassThroughEnabled,
                _edgeRelocationEnabled,
                _hoverVisiblePercent);
            var tempPath = _windowStatePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state));
            File.Move(tempPath, _windowStatePath, overwrite: true);
        }
        catch (IOException)
        {
            // Window-state persistence is optional and must not block shutdown.
        }
        catch (UnauthorizedAccessException)
        {
            // Window-state persistence is optional and must not block shutdown.
        }
        catch (SecurityException)
        {
            // Window-state persistence is optional and must not block shutdown.
        }
        catch (NotSupportedException)
        {
            // Window-state persistence is optional and must not block shutdown.
        }
    }

    private void ScheduleWindowStateSave()
    {
        if (IsDisposed || Disposing) return;
        _stateSaveTimer.Stop();
        _stateSaveTimer.Start();
    }

    private void EnsureInputOverlay()
    {
        if (_inputOverlay is not null) return;
        _inputOverlay = new PetInputOverlay(this)
        {
            Bounds = Bounds,
            TopMost = TopMost
        };
        _inputOverlay.Show(this);
        _statusBubble = new StatusBubbleForm
        {
            TopMost = TopMost
        };
        _statusBubble.SizeChanged += (_, _) => SyncStatusBubbleBounds();
        _statusBubble.TemporaryDismissRequested += (_, _) =>
        {
            if (!_codexTaskActive) return;
            _dismissCodexForCurrentTask = true;
            ApplyStatusBubbleVisibility();
        };
        _noticeBubble = new StatusBubbleForm
        {
            TopMost = TopMost
        };
        _noticeBubble.SizeChanged += (_, _) => SyncNoticeBubbleBounds();
        SyncStatusBubbleBounds();
        ApplyStatusBubbleVisibility();
        ApplyHoverState(Cursor.Position);
    }

    private void SyncInputOverlayBounds()
    {
        if (_inputOverlay is not null && !_inputOverlay.IsDisposed) _inputOverlay.Bounds = Bounds;
        SyncStatusBubbleBounds();
        SyncNoticeBubbleBounds();
    }

    private void SyncStatusBubbleBounds()
    {
        if (_statusBubble is null || _statusBubble.IsDisposed) return;
        const int gap = 8;
        var bubbleSize = _statusBubble.RequiredWindowSize;
        if (_statusBubble.Size != bubbleSize) _statusBubble.Size = bubbleSize;
        var workingArea = Screen.FromRectangle(Bounds).WorkingArea;
        var x = Math.Clamp(
            Left + (Width - bubbleSize.Width) / 2,
            workingArea.Left,
            Math.Max(workingArea.Left, workingArea.Right - bubbleSize.Width));
        var aboveY = Top - bubbleSize.Height - gap;
        var belowY = Bottom + gap;
        var preferAbove = _selectedBubblePosition == "above";
        var y = preferAbove ? aboveY : belowY;

        // Keep the bubble outside the pet. At a screen edge, move it to the
        // other side rather than placing it over the character.
        if (preferAbove && aboveY < workingArea.Top && belowY + bubbleSize.Height <= workingArea.Bottom)
        {
            y = belowY;
        }
        else if (!preferAbove && belowY + bubbleSize.Height > workingArea.Bottom && aboveY >= workingArea.Top)
        {
            y = aboveY;
        }

        _statusBubble.SetAnchor(new Point(x, y), bubbleSize);
    }

    private void SyncNoticeBubbleBounds()
    {
        if (_noticeBubble is null || _noticeBubble.IsDisposed) return;
        const int gap = 8;
        var bubbleSize = _noticeBubble.RequiredWindowSize;
        if (_noticeBubble.Size != bubbleSize) _noticeBubble.Size = bubbleSize;
        var workingArea = Screen.FromRectangle(Bounds).WorkingArea;
        var x = Math.Clamp(
            Left + (Width - bubbleSize.Width) / 2,
            workingArea.Left,
            Math.Max(workingArea.Left, workingArea.Right - bubbleSize.Width));
        var aboveY = Top - bubbleSize.Height - gap;
        var belowY = Bottom + gap;
        var preferAbove = _selectedBubblePosition == "above";
        var y = preferAbove ? aboveY : belowY;
        if (preferAbove && aboveY < workingArea.Top && belowY + bubbleSize.Height <= workingArea.Bottom)
        {
            y = belowY;
        }
        else if (!preferAbove && belowY + bubbleSize.Height > workingArea.Bottom && aboveY >= workingArea.Top)
        {
            y = aboveY;
        }

        _noticeBubble.SetAnchor(new Point(x, y), bubbleSize);
    }

    private sealed record SavedWindowState(
        int X,
        int Y,
        int Size,
        bool TopMost,
        string? Shape,
        string? BubblePosition = null,
        bool? ShowBubble = null,
        bool? ShowCodexBubble = null,
        bool? ShowAppBubble = null,
        bool? HoverPassThrough = null,
        bool? EdgeRelocation = null,
        int? HoverVisiblePercent = null);
}

internal sealed class PetInputOverlay : Form
{
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private bool _clickThrough;

    protected override bool ShowWithoutActivation => true;

    public void SetClickThrough(bool clickThrough)
    {
        _clickThrough = clickThrough;
        if (!IsHandleCreated) return;

        var exStyle = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
        exStyle = clickThrough ? exStyle | WsExTransparent : exStyle & ~WsExTransparent;
        SetWindowLongPtr(Handle, GwlExStyle, new nint(exStyle));
    }

    public PetInputOverlay(PetForm pet)
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 1d / 255d;
        MouseDown += pet.OnNativeMouseDown;
        MouseMove += pet.OnNativeMouseMove;
        MouseUp += pet.OnNativeMouseUp;
        MouseCaptureChanged += pet.OnNativeMouseCaptureChanged;
        MouseWheel += pet.OnNativeMouseWheel;
        MouseDoubleClick += pet.OnNativeMouseDoubleClick;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            var parameters = base.CreateParams;
            parameters.ExStyle |= wsExToolWindow;
            return parameters;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (_clickThrough && message.Msg == WmNcHitTest)
        {
            message.Result = (nint)HtTransparent;
            return;
        }

        base.WndProc(ref message);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}

internal sealed class StatusBubbleForm : Form
{
    private const float CompactFontSize = 20f;
    private const float TaskTitleFontSize = 20f;
    private const float TaskStatusFontSize = 18f;
    private const int CompactHeight = 48;
    private const int MaxCompactHanWidth = 10;
    private const int ShadowLeft = 9;
    private const int ShadowTop = 7;
    private const int ShadowRight = 9;
    private const int ShadowBottom = 11;
    private const int BubbleHorizontalPadding = 32;
    private const int MaxLocalBubbleWidth = 380;
    private const int WrappedLocalHeight = 96;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;
    private static readonly Size TaskSize = new(310, 84);
    private static readonly PrivateFontCollection MediumFonts = LoadPrivateFont("PingFangSC-Medium.ttf");
    private static readonly PrivateFontCollection SemiboldFonts = LoadPrivateFont("PingFangSC-Semibold.ttf");
    private string _label = "正在连接…";
    private string _displayLabel = "正在连接…";
    private string _taskName = string.Empty;
    private string[] _primaryLines = [];
    private Size _bubbleContentSize;
    private bool _showTask;
    private bool _wrapPrimary;
    private bool _temporaryDismissEnabled;
    private bool _closeButtonVisible;
    private bool _clickThrough;
    private int _hoverVisualPercent = 100;
    private Point _layeredLocation;

    public event EventHandler? TemporaryDismissRequested;

    protected override bool ShowWithoutActivation => true;
    public Size RequiredWindowSize => AddShadowPadding(_bubbleContentSize);

    public void SetAnchor(Point location, Size size)
    {
        _layeredLocation = location;
        var bounds = new Rectangle(location, size);
        if (Bounds != bounds) Bounds = bounds;
        if (Visible) RenderLayeredWindow();
    }

    public StatusBubbleForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        _bubbleContentSize = new Size(MeasureCompactWidth(_displayLabel), CompactHeight);
        ClientSize = AddShadowPadding(_bubbleContentSize);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
    }

    public void SetTemporaryDismissEnabled(bool enabled)
    {
        _temporaryDismissEnabled = enabled;
        if (enabled || !_closeButtonVisible) return;
        _closeButtonVisible = false;
        Cursor = Cursors.Default;
        RenderLayeredWindow();
    }

    public void SetClickThrough(bool clickThrough)
    {
        _clickThrough = clickThrough;
        if (!IsHandleCreated) return;

        var exStyle = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
        exStyle = clickThrough ? exStyle | WsExTransparent : exStyle & ~WsExTransparent;
        SetWindowLongPtr(Handle, GwlExStyle, new nint(exStyle));
    }

    public void SetHoverVisualPercent(int visiblePercent)
    {
        visiblePercent = Math.Clamp(visiblePercent, 0, 100);
        if (_hoverVisualPercent == visiblePercent) return;
        _hoverVisualPercent = visiblePercent;
        RenderLayeredWindow();
    }

    public void UpdateStatus(string label, bool online, string? taskName, bool taskActive)
    {
        _label = string.IsNullOrWhiteSpace(label) ? "未知状态" : label;
        _taskName = taskName?.Trim() ?? string.Empty;
        _primaryLines = _taskName.Length > 0 ? [_taskName] : [];
        _showTask = online && taskActive && _taskName.Length > 0;
        _wrapPrimary = false;
        _displayLabel = _showTask ? _label : TruncateToHanWidth(_label);
        var nextSize = _showTask
            ? TaskSize
            : new Size(MeasureCompactWidth(_displayLabel), CompactHeight);
        _bubbleContentSize = nextSize;
        var nextClientSize = AddShadowPadding(nextSize);
        if (ClientSize != nextClientSize) ClientSize = nextClientSize;
        RenderLayeredWindow();
    }

    public void UpdateLocalActivity(string phrase, string state)
    {
        _taskName = string.IsNullOrWhiteSpace(phrase) ? "正在观察这个窗口" : phrase.Trim();
        _label = string.IsNullOrWhiteSpace(state) ? "本地活动中" : state.Trim();
        _displayLabel = _label;
        _showTask = true;
        using var font = CreateFont(BubbleFontWeight.Semibold, TaskTitleFontSize);
        var measuredWidth = MeasureTextWidth(_taskName, font);
        _primaryLines = measuredWidth <= MaxLocalBubbleWidth - BubbleHorizontalPadding * 2
            ? [_taskName]
            : SplitPrimaryText(_taskName, font, MaxLocalBubbleWidth - BubbleHorizontalPadding * 2);
        _wrapPrimary = _primaryLines.Length > 1;
        var requiredTextWidth = _primaryLines.Max(line => MeasureTextWidth(line, font));
        var bubbleWidth = Math.Clamp(
            (int)Math.Ceiling(requiredTextWidth) + BubbleHorizontalPadding * 2,
            TaskSize.Width,
            MaxLocalBubbleWidth);
        var nextSize = new Size(bubbleWidth, _wrapPrimary ? WrappedLocalHeight : TaskSize.Height);
        _bubbleContentSize = nextSize;
        var nextClientSize = AddShadowPadding(nextSize);
        if (ClientSize != nextClientSize) ClientSize = nextClientSize;
        RenderLayeredWindow();
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        RenderLayeredWindow();
    }

    protected override void OnLocationChanged(EventArgs eventArgs)
    {
        base.OnLocationChanged(eventArgs);
        RenderLayeredWindow();
    }

    protected override void OnSizeChanged(EventArgs eventArgs)
    {
        base.OnSizeChanged(eventArgs);
        RenderLayeredWindow();
    }

    protected override void OnPaintBackground(PaintEventArgs eventArgs)
    {
        // Per-pixel alpha is supplied through UpdateLayeredWindow.
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        // Per-pixel alpha is supplied through UpdateLayeredWindow.
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (!_temporaryDismissEnabled || eventArgs.Button != MouseButtons.Left) return;

        if (_closeButtonVisible && GetCloseButtonBounds().Contains(eventArgs.Location))
        {
            _closeButtonVisible = false;
            TemporaryDismissRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!GetBubbleBounds().Contains(eventArgs.Location)) return;
        _closeButtonVisible = true;
        RenderLayeredWindow();
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        Cursor = _closeButtonVisible && GetCloseButtonBounds().Contains(eventArgs.Location)
            ? Cursors.Hand
            : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        if (!_closeButtonVisible) return;
        _closeButtonVisible = false;
        Cursor = Cursors.Default;
        RenderLayeredWindow();
    }

    private void RenderLayeredWindow()
    {
        if (!IsHandleCreated || !Visible || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var visualAlpha = 255 * _hoverVisualPercent / 100;
        if (visualAlpha > 0)
        {
            var bubbleRectangle = new RectangleF(
                ShadowLeft + 0.5f,
                ShadowTop + 0.5f,
                _bubbleContentSize.Width - 1f,
                _bubbleContentSize.Height - 1f);
            var bubbleRadius = _wrapPrimary ? 30f : (_bubbleContentSize.Height - 1f) / 2f;
            DrawBubbleShadow(graphics, bubbleRectangle, bubbleRadius, _hoverVisualPercent / 100f);
            using var bubblePath = CreateRoundedRectangle(bubbleRectangle, bubbleRadius);
            using var fill = new SolidBrush(Color.FromArgb(visualAlpha, 255, 253, 248));
            graphics.FillPath(fill, bubblePath);

            using var primaryBrush = new SolidBrush(Color.FromArgb(visualAlpha, 48, 48, 45));
            using var secondaryBrush = new SolidBrush(Color.FromArgb(visualAlpha, 119, 119, 113));
            using var primaryFont = CreateFont(
                BubbleFontWeight.Semibold,
                _showTask ? TaskTitleFontSize : CompactFontSize);
            using var format = CreateTextFormat();
            if (_showTask)
            {
                using var statusFont = CreateFont(BubbleFontWeight.Medium, TaskStatusFontSize);
                if (_wrapPrimary)
                {
                    graphics.DrawString(_primaryLines[0], primaryFont, primaryBrush, new RectangleF(ShadowLeft + BubbleHorizontalPadding, ShadowTop + 7, _bubbleContentSize.Width - BubbleHorizontalPadding * 2, 28), format);
                    graphics.DrawString(_primaryLines[1], primaryFont, primaryBrush, new RectangleF(ShadowLeft + BubbleHorizontalPadding, ShadowTop + 32, _bubbleContentSize.Width - BubbleHorizontalPadding * 2, 28), format);
                    graphics.DrawString(_displayLabel, statusFont, secondaryBrush, new RectangleF(ShadowLeft + BubbleHorizontalPadding, ShadowTop + 62, _bubbleContentSize.Width - BubbleHorizontalPadding * 2, 26), format);
                }
                else
                {
                    graphics.DrawString(_taskName, primaryFont, primaryBrush, new RectangleF(ShadowLeft + BubbleHorizontalPadding, ShadowTop + 13, _bubbleContentSize.Width - BubbleHorizontalPadding * 2, 29), format);
                    graphics.DrawString(_displayLabel, statusFont, secondaryBrush, new RectangleF(ShadowLeft + BubbleHorizontalPadding, ShadowTop + 43, _bubbleContentSize.Width - BubbleHorizontalPadding * 2, 27), format);
                }
            }
            else
            {
                using var compactFormat = CreateTextFormat(StringAlignment.Center);
                graphics.DrawString(
                    _displayLabel,
                    primaryFont,
                    primaryBrush,
                    new RectangleF(ShadowLeft, ShadowTop, _bubbleContentSize.Width, _bubbleContentSize.Height),
                    compactFormat);
            }

            if (_temporaryDismissEnabled && _closeButtonVisible) DrawCloseButton(graphics);
        }

        ApplyLayeredBitmap(bitmap);
    }

    private RectangleF GetBubbleBounds() => new(
        ShadowLeft,
        ShadowTop,
        _bubbleContentSize.Width,
        _bubbleContentSize.Height);

    private static RectangleF GetCloseButtonBounds() => new(
        ShadowLeft - 7,
        ShadowTop - 7,
        26,
        26);

    private static void DrawCloseButton(Graphics graphics)
    {
        var bounds = GetCloseButtonBounds();
        using var shadow = new SolidBrush(Color.FromArgb(28, 22, 28, 34));
        graphics.FillEllipse(shadow, bounds.X, bounds.Y + 2, bounds.Width, bounds.Height);
        using var fill = new SolidBrush(Color.FromArgb(248, 239, 242, 241));
        graphics.FillEllipse(fill, bounds);

        using var pen = new Pen(Color.FromArgb(54, 58, 57), 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var inset = 8f;
        graphics.DrawLine(pen, bounds.Left + inset, bounds.Top + inset, bounds.Right - inset, bounds.Bottom - inset);
        graphics.DrawLine(pen, bounds.Right - inset, bounds.Top + inset, bounds.Left + inset, bounds.Bottom - inset);
    }

    private void ApplyLayeredBitmap(Bitmap bitmap)
    {
        var screenDc = GetDC(nint.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
        var oldBitmap = SelectObject(memoryDc, bitmapHandle);
        try
        {
            var destination = new NativePoint(_layeredLocation.X, _layeredLocation.Y);
            var size = new NativeSize(bitmap.Width, bitmap.Height);
            var source = new NativePoint(0, 0);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha
            };
            UpdateLayeredWindow(
                Handle,
                screenDc,
                ref destination,
                ref size,
                memoryDc,
                ref source,
                0,
                ref blend,
                UlwAlpha);
        }
        finally
        {
            SelectObject(memoryDc, oldBitmap);
            DeleteObject(bitmapHandle);
            DeleteDC(memoryDc);
            ReleaseDC(nint.Zero, screenDc);
        }
    }

    private static int MeasureCompactWidth(string text)
    {
        using var font = CreateFont(BubbleFontWeight.Semibold, CompactFontSize);
        var measured = (int)Math.Ceiling(MeasureTextWidth(text, font));
        var maximum = (int)Math.Ceiling(MeasureTextWidth(new string('汉', MaxCompactHanWidth), font));
        return Math.Clamp(measured + 64, 108, maximum + 64);
    }

    private static Size AddShadowPadding(Size contentSize) => new(
        contentSize.Width + ShadowLeft + ShadowRight,
        contentSize.Height + ShadowTop + ShadowBottom);

    private static void DrawBubbleShadow(Graphics graphics, RectangleF bubbleRectangle, float radius, float alphaScale = 1f)
    {
        var shadowRectangle = bubbleRectangle;
        shadowRectangle.Offset(0, 2f);
        using var shadowPath = CreateRoundedRectangle(shadowRectangle, radius);
        foreach (var layer in new[]
                 {
                     (Width: 16f, Alpha: 6),
                     (Width: 12f, Alpha: 8),
                     (Width: 8f, Alpha: 10),
                     (Width: 4f, Alpha: 13)
                 })
        {
            using var pen = new Pen(Color.FromArgb(Math.Clamp((int)Math.Round(layer.Alpha * alphaScale), 1, 255), 24, 31, 38), layer.Width)
            {
                LineJoin = LineJoin.Round
            };
            graphics.DrawPath(pen, shadowPath);
        }
    }

    private static string TruncateToHanWidth(string value)
    {
        using var font = CreateFont(BubbleFontWeight.Semibold, CompactFontSize);
        var maximum = MeasureTextWidth(new string('汉', MaxCompactHanWidth), font);
        if (MeasureTextWidth(value, font) <= maximum) return value;

        var result = string.Empty;
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            var next = result + enumerator.GetTextElement();
            if (MeasureTextWidth(next + "…", font) > maximum) break;
            result = next;
        }
        return result + "…";
    }

    private static float MeasureTextWidth(string text, Font font)
    {
        using var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var format = CreateTextFormat();
        return graphics.MeasureString(text, font, new SizeF(10000, CompactHeight), format).Width;
    }

    private static string[] SplitPrimaryText(string text, Font font, float maximumWidth)
    {
        if (MeasureTextWidth(text, font) <= maximumWidth) return [text];

        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());

        var bestFirst = string.Empty;
        var bestSecond = string.Empty;
        var bestDifference = float.MaxValue;
        for (var index = 1; index < elements.Count; index++)
        {
            if (!IsNaturalLineBreak(elements[index - 1], elements[index])) continue;
            var first = string.Concat(elements.Take(index));
            var second = string.Concat(elements.Skip(index));
            var firstWidth = MeasureTextWidth(first, font);
            var secondWidth = MeasureTextWidth(second, font);
            if (firstWidth > maximumWidth || secondWidth > maximumWidth) continue;

            var difference = Math.Abs(firstWidth - secondWidth);
            if (difference >= bestDifference) continue;
            bestDifference = difference;
            bestFirst = first;
            bestSecond = second;
        }

        if (bestFirst.Length > 0) return [bestFirst, bestSecond];

        var firstLine = FitTextToWidth(elements, font, maximumWidth, addEllipsis: false, out var consumed);
        var remaining = elements.Skip(consumed).ToList();
        var secondLine = FitTextToWidth(remaining, font, maximumWidth, addEllipsis: true, out _);
        return [firstLine, secondLine];
    }

    private static bool IsNaturalLineBreak(string previous, string next)
    {
        if (IsAsciiWordElement(previous) && IsAsciiWordElement(next)) return false;
        if ("「『（【《〈“‘([{<".Contains(previous, StringComparison.Ordinal)) return false;
        if ("」』）】》〉”’、，。！？：；,.!?;:)]}>".Contains(next, StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool IsAsciiWordElement(string element) =>
        element.All(character => character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '_' or '-');

    private static string FitTextToWidth(
        IReadOnlyList<string> elements,
        Font font,
        float maximumWidth,
        bool addEllipsis,
        out int consumed)
    {
        var result = string.Empty;
        consumed = 0;
        foreach (var element in elements)
        {
            var suffix = addEllipsis && consumed + 1 < elements.Count ? "…" : string.Empty;
            if (MeasureTextWidth(result + element + suffix, font) > maximumWidth) break;
            result += element;
            consumed++;
        }
        return addEllipsis && consumed < elements.Count ? result + "…" : result;
    }

    private static StringFormat CreateTextFormat(StringAlignment alignment = StringAlignment.Near) => new()
    {
        Alignment = alignment,
        LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap
    };

    private static PrivateFontCollection LoadPrivateFont(string fileName)
    {
        var collection = new PrivateFontCollection();
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fonts", fileName);
            if (File.Exists(path)) collection.AddFontFile(path);
        }
        catch (Exception error) when (error is ArgumentException or FileNotFoundException)
        {
            // The installed PingFang fallback below keeps the pet usable if a
            // packaged font file is missing or cannot be loaded.
        }
        return collection;
    }

    private static Font CreateFont(BubbleFontWeight weight, float size)
    {
        var collection = weight == BubbleFontWeight.Semibold ? SemiboldFonts : MediumFonts;
        if (collection.Families.Length > 0)
        {
            return new Font(collection.Families[0], size, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        try
        {
            var style = weight == BubbleFontWeight.Semibold ? FontStyle.Bold : FontStyle.Regular;
            return new Font("苹方_中等", size, style, GraphicsUnit.Pixel);
        }
        catch (ArgumentException)
        {
            var style = weight == BubbleFontWeight.Semibold ? FontStyle.Bold : FontStyle.Regular;
            return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Pixel);
        }
    }

    private enum BubbleFontWeight
    {
        Medium,
        Semibold
    }

    private static GraphicsPath CreateRoundedRectangle(RectangleF rectangle, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            const int wsExNoActivate = 0x08000000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= wsExToolWindow | wsExNoActivate | WsExLayered;
            return parameters;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (_clickThrough && message.Msg == WmNcHitTest)
        {
            message.Result = (nint)HtTransparent;
            return;
        }

        base.WndProc(ref message);
        if (message.Msg != WmNcHitTest || message.Result == nint.Zero) return;

        var packed = message.LParam.ToInt64();
        var screenPoint = new Point(
            unchecked((short)(packed & 0xffff)),
            unchecked((short)((packed >> 16) & 0xffff)));
        var clientPoint = PointToClient(screenPoint);
        var hitClose = _temporaryDismissEnabled
            && _closeButtonVisible
            && GetCloseButtonBounds().Contains(clientPoint);
        if (!hitClose && !GetBubbleBounds().Contains(clientPoint))
        {
            message.Result = (nint)HtTransparent;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint deviceContext, nint graphicsObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint graphicsObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        nint window,
        nint destinationDeviceContext,
        ref NativePoint destination,
        ref NativeSize size,
        nint sourceDeviceContext,
        ref NativePoint source,
        int colorKey,
        ref BlendFunction blend,
        int flags);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(int Width, int Height);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }
}

internal sealed class BridgeProcessManager : IDisposable
{
    private readonly string _projectRoot;
    private readonly int _port;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _ownedProcess;
    private bool _disposed;

    public BridgeProcessManager(string projectRoot, int port)
    {
        _projectRoot = projectRoot;
        _port = port;
    }

    public async Task<bool> EnsureRunningAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return false;
            if (await IsHealthyAsync()) return false;
            if (_disposed) return false;

            var serverPath = Path.Combine(_projectRoot, "bridge", "server.mjs");
            var runtimeDirectory = Path.Combine(_projectRoot, ".bridge-runtime");
            var bundledNodePath = Path.Combine(_projectRoot, "runtime", "node.exe");
            var runtimeDirectoryExists = Directory.Exists(Path.Combine(_projectRoot, "runtime"));
            if (runtimeDirectoryExists && !File.Exists(bundledNodePath))
            {
                throw new FileNotFoundException("打包运行时缺少 runtime/node.exe。", bundledNodePath);
            }
            StopOwnedProcess();
            if (_disposed) return false;
            Directory.CreateDirectory(runtimeDirectory);

            var startInfo = new ProcessStartInfo
            {
                FileName = runtimeDirectoryExists ? bundledNodePath : "node",
                WorkingDirectory = _projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(serverPath);
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(_port.ToString());
            _ownedProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the local Codex bridge.");

            _ = DrainAsync(_ownedProcess.StandardOutput, Path.Combine(runtimeDirectory, "desktop-server.log"));
            _ = DrainAsync(_ownedProcess.StandardError, Path.Combine(runtimeDirectory, "desktop-server-error.log"));

            for (var attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(200);
                if (_disposed) return false;
                if (_ownedProcess is null || _ownedProcess.HasExited) break;
                if (await IsHealthyAsync()) return true;
            }
            StopOwnedProcess();
            throw new InvalidOperationException("The local Codex bridge did not become ready.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> IsHealthyAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(700) };
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{_port}/api/health");
            if (!response.IsSuccessStatusCode) return false;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.True
                && document.RootElement.TryGetProperty("bridgeVersion", out var version)
                && version.ValueKind == JsonValueKind.Number
                && version.GetInt32() >= 2;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task DrainAsync(StreamReader reader, string logPath)
    {
        try
        {
            const int maxLogCharacters = 1_000_000;
            await using var file = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            await using var writer = new StreamWriter(file);
            var remaining = maxLogCharacters;
            while (await reader.ReadLineAsync() is { } line)
            {
                if (remaining <= 0) continue;
                var text = line.Length + Environment.NewLine.Length;
                if (text > remaining)
                {
                    await writer.WriteLineAsync(line[..Math.Max(0, remaining - Environment.NewLine.Length)]);
                    remaining = 0;
                }
                else
                {
                    await writer.WriteLineAsync(line);
                    remaining -= text;
                }
            }
        }
        catch
        {
            // Logging must never prevent the desktop pet from closing.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        StopOwnedProcess();
    }

    private void StopOwnedProcess()
    {
        if (_ownedProcess is null) return;
        try
        {
            if (!_ownedProcess.HasExited) _ownedProcess.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Process teardown is best-effort during shutdown/recovery.
        }
        finally
        {
            _ownedProcess.Dispose();
            _ownedProcess = null;
        }
    }
}
