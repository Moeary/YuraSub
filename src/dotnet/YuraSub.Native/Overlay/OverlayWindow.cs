using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YuraSub.Native.Interop;
using YuraSub.Native.Json;

namespace YuraSub.Native.Overlay;

/// <summary>
/// Main overlay window with Direct2D rendering, drag/resize, lock/click-through.
/// Uses a DC render target + UpdateLayeredWindow for true per-pixel alpha transparency.
/// </summary>
internal sealed class OverlayWindow : IDisposable
{
    // --- State ---
    private readonly JsonObject _config;
    private JsonObject _style;
    private readonly HashSet<string> _localOverrideKeys = new(StringComparer.Ordinal);
    private string _text = "";
    private string _translation = "";
    private bool _clickThrough;
    private bool _locked;
    private bool _statusActive;

    // Media state
    private double _mediaDuration;
    private double _mediaPosition;
    private bool _mediaPaused = true;

    // Drag/resize
    private string? _dragMode;
    private int _dragStartX, _dragStartY;
    private int _dragStartLeft, _dragStartTop, _dragStartWidth, _dragStartHeight;

    // Local geometry override — suppresses remote geometry after manual drag/resize
    private bool _localGeometryOverride;

    // Style defaults
    private static readonly JsonObject DefaultStyle = new()
    {
        ["fontFamily"] = Config.Defaults.FontFamily,
        ["fontSize"] = Config.Defaults.FontSize,
        ["translationFontSize"] = Config.Defaults.TranslationFontSize,
        ["textColor"] = Config.Defaults.TextColor,
        ["textOpacity"] = Config.Defaults.TextOpacity,
        ["translationColor"] = "#bfefff",
        ["translationOpacity"] = 100,
        ["outlineColor"] = Config.Defaults.OutlineColor,
        ["outlineWidth"] = Config.Defaults.OutlineWidth,
        ["outlineOpacity"] = Config.Defaults.OutlineOpacity,
        ["shadowColor"] = "#000000a0",
        ["shadowOpacity"] = 65,
        ["shadowOffsetX"] = 2,
        ["shadowOffsetY"] = 3,
        ["backgroundColor"] = Config.Defaults.BackgroundColor,
        ["backgroundOpacity"] = Config.Defaults.BackgroundOpacity,
        ["backgroundRadius"] = 12,
        ["paddingX"] = 18,
        ["paddingY"] = 12,
        ["lineGap"] = 6,
        ["align"] = "center",
        ["maxLines"] = 4,
    };

    private static readonly string[] ColorPalette =
    {
        "#ffffffff",
        "#f5fff8e6",
        "#7dd3fccc",
        "#00a0a3e6",
        "#ff05deff",
        "#f7ff0eff",
        "#101522ff",
    };

    // Control color
    private string _controlColorHex = Config.Defaults.ControlColor;
    private int _controlOpacityPercent = Config.Defaults.ControlOpacity;
    private string _controlBackgroundColorHex = Config.Defaults.ControlBackgroundColor;
    private int _controlBackgroundOpacityPercent = Config.Defaults.ControlBackgroundOpacity;

    // Win32
    private IntPtr _hwnd;
    private IntPtr _hInstance;
    private Win32.WndProcDelegate? _wndProc; // prevent GC

    // Direct2D — DC render target (for UpdateLayeredWindow)
    private IntPtr _d2dFactory;
    private IntPtr _dwriteFactory;
    private IntPtr _dcRenderTarget;
    private int _dcRTWidth;
    private int _dcRTHeight;
    private bool _d2dInitialized;

    // DIB section for UpdateLayeredWindow
    private IntPtr _dibSection;
    private IntPtr _dibPixels;
    private IntPtr _dibDC;
    private IntPtr _memDC;

    // Rendering mode: true = ULW (transparent), false = HWND RT (black bg, visible)
    private bool _useUlw = true;
    private int _ulwZeroAlphaCount;
    private int _renderRetryCount;

    // D2DERR_RECREATE_TARGET — recoverable, not fatal
    private const int D2DERR_RECREATE_TARGET = unchecked((int)0x88990001);

    // HWND render target (fallback path)
    private IntPtr _renderTarget;
    private int _renderTargetWidth;
    private int _renderTargetHeight;

    // Active draw target — set to _dcRenderTarget (ULW) or _renderTarget (HWND RT) before drawing
    private IntPtr _drawRt;

    // Custom colors for ChooseColorW (16 COLORREF entries, pinned)
    private int[] _custColors = new int[16];

    // Events
    public event Action<string>? OnMediaCommand;
    public event Action<double>? OnMediaSeek;
    public event Action<bool>? OnClickThroughChanged;
    public event Action<bool>? OnLockChanged;
    public event Action<JsonObject>? OnStyleChanged;
    public event Action<string>? OnLog;

    // Log file path (always writable, even for WinExe without console)
    private static readonly string LogFilePath = System.IO.Path.Combine(AppContext.BaseDirectory, "yurasub-native.log");

    private static void LogToFile(string msg)
    {
        try { System.IO.File.AppendAllText(LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n"); }
        catch { }
    }

    private static void ShowError(string msg)
    {
        Win32.MessageBoxW(IntPtr.Zero, msg, "YuraSub", Win32.MB_OK | Win32.MB_ICONERROR);
    }

    // Tray menu IDs
    private const int TRAY_ID_TOGGLE_INTERACTIVE = 1001;
    private const int TRAY_ID_LOCK = 1002;
    private const int TRAY_ID_CLEAR = 1003;
    private const int TRAY_ID_SHOW = 1004;
    private const int TRAY_ID_RESTORE_DEFAULTS = 1005;
    private const int TRAY_ID_QUIT = 1006;

    // Control button bounds (for hit testing)
    private struct ControlButton
    {
        public string Label;
        public string Tooltip;
        public string Command;
        public float X, Y, W, H;
    }

    private readonly List<ControlButton> _controlButtons = new();
    private float _sliderX, _sliderY, _sliderW, _sliderH;
    private bool _sliderDragging;
    private bool _stylePanelVisible;
    private bool _hoveringSlider;
    private bool _hoveringResize;

    public IntPtr Hwnd => _hwnd;
    public bool ClickThrough => _clickThrough;
    public bool Locked => _locked;
    private bool HasVisibleText => !string.IsNullOrEmpty(_text) || !string.IsNullOrEmpty(_translation);

    public OverlayWindow(JsonObject config)
    {
        _config = config;
        _style = new JsonObject();
        foreach (var kv in DefaultStyle) _style[kv.Key] = kv.Value;

        // Restore style from config
        if (config.TryGetValue("style", out var s) && s is JsonObject styleObj)
        {
            foreach (var kv in styleObj)
                _style[kv.Key] = kv.Value;
            _localOverrideKeys.Clear();
            foreach (var kv in styleObj)
                _localOverrideKeys.Add(kv.Key);
        }

        // Restore control colors.
        _controlColorHex = Config.GetString(config, "style", "controlColor", Config.Defaults.ControlColor);
        _controlOpacityPercent = Config.GetInt(config, "style", "controlOpacity", Config.Defaults.ControlOpacity);
        _controlBackgroundColorHex = Config.GetString(config, "style", "controlBackgroundColor", Config.Defaults.ControlBackgroundColor);
        _controlBackgroundOpacityPercent = Config.GetInt(config, "style", "controlBackgroundOpacity", Config.Defaults.ControlBackgroundOpacity);
    }

    public bool Create()
    {
        _hInstance = Win32.GetModuleHandleW(null);
        _wndProc = WndProc;

        string className = "YuraSubOverlay";
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            style = 0x0003, // CS_HREDRAW | CS_VREDRAW
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            lpszClassName = className,
            hCursor = Win32.LoadCursorW(IntPtr.Zero, Win32.IDC_ARROW),
        };
        Win32.RegisterClassExW(ref wc);

        // Create layered, topmost, tool window
        uint exStyle = Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST | Win32.WS_EX_NOACTIVATE;
        uint style = Win32.WS_POPUP | Win32.WS_VISIBLE;

        // Restore geometry from config
        int winW = Config.GetInt(_config, "window", "width", Config.Defaults.WindowWidth);
        int winH = Config.GetInt(_config, "window", "height", Config.Defaults.WindowHeight);
        winW = Math.Max(280, winW);
        winH = Math.Max(80, winH);

        int winX, winY;
        bool hasExplicitPos = false;
        if (_config.TryGetValue("window", out var w) && w is JsonObject winObj &&
            winObj.TryGetValue("x", out var xVal) && xVal is not JsonNull &&
            winObj.TryGetValue("y", out var yVal) && yVal is not JsonNull)
        {
            winX = xVal is JsonNumber xn ? xn.ToInt() : int.MinValue;
            winY = yVal is JsonNumber yn ? yn.ToInt() : int.MinValue;
            if (winX != int.MinValue && winY != int.MinValue)
                hasExplicitPos = true;
        }
        else
        {
            winX = int.MinValue;
            winY = int.MinValue;
        }

        // Validate position against primary monitor
        ClampToMonitor(ref winX, ref winY, winW, winH);

        _hwnd = Win32.CreateWindowExW(exStyle, className, "YuraSub", style,
            winX, winY, winW, winH, IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return false;

        // Restore lock state
        if (_config.TryGetValue("window", out var winCfg) && winCfg is JsonObject wObj)
        {
            bool locked = Payload.ReadBool(wObj.TryGetValue("locked", out var lk) ? lk : null) ?? false;
            if (locked) SetLocked(true);
            else
                SetClickThrough(false);
        }

        // If config had explicit x/y, mark as local override
        if (hasExplicitPos)
            _localGeometryOverride = true;

        InitD2D();
        Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
        RenderFrame();
        return true;
    }

    // --- D2D / DIB initialization ---

    private void InitD2D()
    {
        try
        {
            _d2dFactory = D2DFactory.CreateD2DFactory();
            _dwriteFactory = D2DFactory.CreateDWriteFactory();
            _d2dInitialized = true;

            // Try ULW path first (transparent)
            if (_useUlw)
            {
                try
                {
                    RecreateRenderSurface();
                    LogToFile("ULW render surface created successfully");
                }
                catch (Exception ulwEx)
                {
                    LogToFile($"ULW init FAILED, falling back to HWND RT: {ulwEx.Message}");
                    OnLog?.Invoke($"ULW init failed: {ulwEx.Message}");
                    _useUlw = false;
                    ReleaseRenderSurface();
                }
            }

            // Fallback: HWND render target (black background, always visible)
            if (!_useUlw)
            {
                Win32.GetClientRect(_hwnd, out var rect);
                int w = Math.Max(1, rect.Right - rect.Left);
                int h = Math.Max(1, rect.Bottom - rect.Top);
                _renderTarget = D2DFactory.CreateHwndRenderTarget(_d2dFactory, _hwnd, w, h);
                _renderTargetWidth = w;
                _renderTargetHeight = h;
                D2DFactory.SetTextAntialiasMode(_renderTarget, 1);
                LogToFile($"HWND render target created: {w}x{h}");
            }
        }
        catch (Exception ex)
        {
            LogToFile($"D2D init FAILED completely: {ex.Message}");
            OnLog?.Invoke($"D2D init failed: {ex.Message}");
            _d2dInitialized = false;
            ShowError($"YuraSub D2D init failed:\n{ex.Message}\n\nThe overlay may not render correctly.");
        }
    }

    /// <summary>
    /// Recreate DIB, memory DC, and DC render target when window size changes.
    /// </summary>
    private void RecreateRenderSurface()
    {
        Win32.GetClientRect(_hwnd, out var rect);
        int w = Math.Max(1, rect.Right - rect.Left);
        int h = Math.Max(1, rect.Bottom - rect.Top);

        if (_dcRTWidth == w && _dcRTHeight == h && _dcRenderTarget != IntPtr.Zero)
            return; // No size change

        // Release old resources
        ReleaseRenderSurface();

        _dcRTWidth = w;
        _dcRTHeight = h;

        // Create DIB section (32-bit BGRA, top-down)
        var bmi = new Win32.BITMAPINFO
        {
            bmiHeader = new Win32.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // Top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = (int)Win32.BI_RGB,
            }
        };
        _dibSection = Win32.CreateDIBSection(IntPtr.Zero, ref bmi, Win32.DIB_RGB_COLORS, out _dibPixels, IntPtr.Zero, 0);
        if (_dibSection == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"CreateDIBSection failed: size={w}x{h}, error={err}");
        }

        // Create memory DC and select DIB
        _dibDC = Win32.GetDC(IntPtr.Zero);
        if (_dibDC == IntPtr.Zero)
            throw new InvalidOperationException("GetDC(screen) failed");

        _memDC = Win32.CreateCompatibleDC(_dibDC);
        if (_memDC == IntPtr.Zero)
            throw new InvalidOperationException("CreateCompatibleDC failed");

        IntPtr oldBmp = Win32.SelectObject(_memDC, _dibSection);
        if (oldBmp == IntPtr.Zero)
            throw new InvalidOperationException("SelectObject(DIB) failed");

        // Create DC render target
        var rtProps = new D2D1_RENDER_TARGET_PROPERTIES
        {
            Type = 0,
            PixelFormat = new D2D1_PIXEL_FORMAT
            {
                Format = 87, // DXGI_FORMAT_B8G8R8A8_UNORM
                AlphaMode = 1, // D2D1_ALPHA_MODE_PREMULTIPLIED
            },
            DpiX = 0, DpiY = 0,
            Usage = 0, MinLevel = 0,
        };
        _dcRenderTarget = D2DFactory.CreateDCRenderTarget(_d2dFactory, rtProps);
        D2DFactory.SetTextAntialiasMode(_dcRenderTarget, 1); // CLEARTYPE

        // Bind to memory DC
        var bindRect = new Win32.RECT { Left = 0, Top = 0, Right = w, Bottom = h };
        D2DFactory.BindDC(_dcRenderTarget, _memDC, ref bindRect);

        OnLog?.Invoke($"Render surface created: {w}x{h}, DIB=0x{_dibSection:X}, memDC=0x{_memDC:X}, dcRT=0x{_dcRenderTarget:X}");
    }

    private void ReleaseRenderSurface()
    {
        if (_dcRenderTarget != IntPtr.Zero) { D2DFactory.Release(_dcRenderTarget); _dcRenderTarget = IntPtr.Zero; }
        if (_memDC != IntPtr.Zero) { Win32.DeleteDC(_memDC); _memDC = IntPtr.Zero; }
        if (_dibDC != IntPtr.Zero) { Win32.ReleaseDC(IntPtr.Zero, _dibDC); _dibDC = IntPtr.Zero; }
        if (_dibSection != IntPtr.Zero) { Win32.DeleteObject(_dibSection); _dibSection = IntPtr.Zero; }
        _dibPixels = IntPtr.Zero;
    }

    public void Show()
    {
        Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
        RenderFrame();
    }

    public void Hide() => Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
    public void Raise()
    {
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_SHOWWINDOW);
        RenderFrame();
    }

    private void RequestRepaint()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_useUlw)
            RenderFrame();
        else
        {
            Win32.InvalidateRect(_hwnd, IntPtr.Zero, false);
            Win32.UpdateWindow(_hwnd);
        }
    }

    public void SetSubtitle(string text, string translation)
    {
        _text = Payload.CleanText(text);
        _translation = Payload.CleanText(translation);
        RequestRepaint();
    }

    public void ClearSubtitle()
    {
        _statusActive = false;
        Win32.KillTimer(_hwnd, Win32.TIMER_STATUS);
        _text = "";
        _translation = "";
        RequestRepaint();
    }

    public void ShowStatus(string text, string secondary, int timeoutMs = 4000)
    {
        _statusActive = true;
        SetSubtitle(text, secondary);
        if (timeoutMs > 0)
        {
            Win32.KillTimer(_hwnd, Win32.TIMER_STATUS);
            Win32.SetTimer(_hwnd, Win32.TIMER_STATUS, (uint)timeoutMs, IntPtr.Zero);
        }
    }

    public void SetClickThrough(bool enabled)
    {
        if (_clickThrough == enabled) return;
        _clickThrough = enabled;
        int exStyle = Win32.GetWindowLongW(_hwnd, Win32.GWL_EXSTYLE);
        if (enabled)
            exStyle |= (int)Win32.WS_EX_TRANSPARENT;
        else
            exStyle &= ~(int)Win32.WS_EX_TRANSPARENT;
        Win32.SetWindowLongW(_hwnd, Win32.GWL_EXSTYLE, (uint)exStyle);
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
        RequestRepaint();
        OnClickThroughChanged?.Invoke(enabled);
    }

    public void SetLocked(bool enabled)
    {
        if (_locked == enabled) return;
        _locked = enabled;
        SetClickThrough(enabled);
        OnLockChanged?.Invoke(enabled);
        RequestRepaint();
    }

    public void UnlockForEditing()
    {
        SetLocked(false);
        SetClickThrough(false);
        Show();
        Raise();
        RequestRepaint();
    }

    public void ApplyPayload(JsonObject payload)
    {
        // Apply style
        var style = Payload.ExtractStyle(payload);
        if (style != null) ApplyStyle(style, false);

        // Apply commands (geometry guarded by _localGeometryOverride)
        ApplyCommand(payload);

        // Apply media
        if (payload.TryGetValue("media", out var m) && m is JsonObject mediaObj)
            ApplyMedia(mediaObj);

        // Apply text
        var (text, translation) = Payload.PickText(payload);
        _statusActive = false;
        if (!string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(translation))
            SetSubtitle(text, translation);
        else
            ClearSubtitle();
    }

    public void ApplyStyle(JsonObject style, bool local)
    {
        foreach (var kv in style)
        {
            if (local)
            {
                _localOverrideKeys.Add(kv.Key);
                _style[kv.Key] = kv.Value;
            }
            else
            {
                if (!_localOverrideKeys.Contains(kv.Key))
                    _style[kv.Key] = kv.Value;
            }
        }
        OnStyleChanged?.Invoke(_style);
        RequestRepaint();
    }

    public void ApplyCommand(JsonObject payload)
    {
        // clickThrough / click_through
        foreach (string key in new[] { "clickThrough", "click_through" })
        {
            if (payload.TryGetValue(key, out var v))
            {
                bool? parsed = Payload.ReadBool(v);
                if (parsed.HasValue) SetClickThrough(parsed.Value);
            }
        }

        // interactive
        if (payload.TryGetValue("interactive", out var inter))
        {
            bool? parsed = Payload.ReadBool(inter);
            if (parsed.HasValue) SetClickThrough(!parsed.Value);
        }

        // geometry — guarded by local override (like Python _localGeometry_override)
        if (payload.TryGetValue("geometry", out var g) && g is JsonObject geo)
        {
            if (!_localGeometryOverride)
                ApplyGeometry(geo);
        }
    }

    private void ApplyMedia(JsonObject media)
    {
        double duration = media.TryGetValue("duration", out var d) && d is JsonNumber dn ? dn.Value : 0;
        double position = media.TryGetValue("currentTime", out var p) && p is JsonNumber pn ? pn.Value : 0;
        if (!double.IsFinite(duration) || duration <= 0) duration = 0;
        if (!double.IsFinite(position)) position = 0;
        if (duration > 0) position = Math.Max(0, Math.Min(position, duration));
        else position = Math.Max(0, position);
        _mediaDuration = duration;
        _mediaPosition = position;
        _mediaPaused = Payload.ReadBool(media.TryGetValue("paused", out var pa) ? pa : null) ?? true;
        RequestRepaint();
    }

    private void ApplyGeometry(JsonObject geometry)
    {
        int w = geometry.TryGetValue("width", out var gw) && gw is JsonNumber gwn ? gwn.ToInt() : 1100;
        int h = geometry.TryGetValue("height", out var gh) && gh is JsonNumber ghn ? ghn.ToInt() : 180;
        w = Math.Max(280, Math.Min(w, 3840));
        h = Math.Max(80, Math.Min(h, 2160));

        if (geometry.TryGetValue("x", out var gx) && geometry.TryGetValue("y", out var gy))
        {
            int x = gx is JsonNumber gxn ? gxn.ToInt() : int.MinValue;
            int y = gy is JsonNumber gyn ? gyn.ToInt() : int.MinValue;
            ClampToMonitor(ref x, ref y, w, h);
            Win32.MoveWindow(_hwnd, x, y, w, h, true);
            RequestRepaint();
            return;
        }

        string anchor = geometry.TryGetValue("anchor", out var a) ? a.ToString() : "bottom";
        int marginBottom = geometry.TryGetValue("marginBottom", out var mb) && mb is JsonNumber mbn ? mbn.ToInt() : 80;

        var primary = GetPrimaryMonitorWorkArea();
        int screenW = primary.Right - primary.Left;
        int screenH = primary.Bottom - primary.Top;

        int x2 = primary.Left + (screenW - w) / 2;
        int y2 = anchor == "top" ? primary.Top + 80 : primary.Bottom - h - marginBottom;
        Win32.MoveWindow(_hwnd, x2, y2, w, h, true);
        RequestRepaint();
    }

    public JsonObject SaveState()
    {
        Win32.GetWindowRect(_hwnd, out var rect);
        int width = Math.Max(280, rect.Right - rect.Left);
        int height = Math.Max(80, rect.Bottom - rect.Top);
        int x = rect.Left;
        int y = rect.Top;
        // Ensure saved position is on-screen
        ClampToMonitor(ref x, ref y, width, height);
        var style = new JsonObject();
        foreach (var kv in _style) style[kv.Key] = kv.Value;
        style["controlColor"] = _controlColorHex;
        style["controlOpacity"] = _controlOpacityPercent;
        style["controlBackgroundColor"] = _controlBackgroundColorHex;
        style["controlBackgroundOpacity"] = _controlBackgroundOpacityPercent;

        return new JsonObject
        {
            ["schemaVersion"] = Config.SchemaVersion,
            ["server"] = _config.TryGetValue("server", out var srv) ? srv : new JsonObject
            {
                ["websocketPort"] = Config.Defaults.WebSocketPort,
                ["httpPort"] = Config.Defaults.HttpPort,
            },
            ["window"] = new JsonObject
            {
                ["x"] = x,
                ["y"] = y,
                ["width"] = width,
                ["height"] = height,
                ["clickThrough"] = _clickThrough,
                ["locked"] = _locked,
            },
            ["style"] = style,
        };
    }

    public void ResetToDefaults()
    {
        _config.Clear();
        _style = new JsonObject();
        foreach (var kv in DefaultStyle) _style[kv.Key] = kv.Value;
        _localOverrideKeys.Clear();
        foreach (var kv in DefaultStyle) _localOverrideKeys.Add(kv.Key);
        _controlColorHex = Config.Defaults.ControlColor;
        _controlOpacityPercent = Config.Defaults.ControlOpacity;
        _controlBackgroundColorHex = Config.Defaults.ControlBackgroundColor;
        _controlBackgroundOpacityPercent = Config.Defaults.ControlBackgroundOpacity;
        // Reset geometry override so remote geometry can apply again
        _localGeometryOverride = false;
        UnlockForEditing();

        var primary = GetPrimaryMonitorWorkArea();
        int screenW = primary.Right - primary.Left;
        int w = Math.Min(Config.Defaults.WindowWidth, (int)(screenW * 0.86));
        int x = primary.Left + (screenW - w) / 2;
        int y = primary.Bottom - Config.Defaults.WindowHeight - 80;
        Win32.MoveWindow(_hwnd, x, y, w, Config.Defaults.WindowHeight, true);
        RequestRepaint();
        OnStyleChanged?.Invoke(_style);
    }

    // --- WndProc ---
    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_PAINT:
                if (_useUlw)
                    Win32.ValidateRect(_hwnd, IntPtr.Zero); // ULW path: validate only
                else
                    OnPaint(); // HWND RT path: paint normally
                return IntPtr.Zero;

            case Win32.WM_LBUTTONDOWN:
                OnLButtonDown(wParam, lParam);
                return IntPtr.Zero;

            case Win32.WM_LBUTTONUP:
                OnLButtonUp(wParam, lParam);
                return IntPtr.Zero;

            case Win32.WM_LBUTTONDBLCLK:
                SetLocked(true);
                return IntPtr.Zero;

            case Win32.WM_MOUSEMOVE:
                OnMouseMove(wParam, lParam);
                return IntPtr.Zero;

            case Win32.WM_RBUTTONUP:
                ShowTrayMenu();
                return IntPtr.Zero;

            case Win32.WM_TIMER:
                if ((uint)wParam == Win32.TIMER_STATUS && _statusActive)
                    ClearSubtitle();
                Win32.KillTimer(_hwnd, (uint)wParam);
                return IntPtr.Zero;

            case Win32.WM_TRAYICON:
                OnTrayMessage(lParam);
                return IntPtr.Zero;

            case Win32.WM_COMMAND:
                OnTrayCommand((int)(wParam & 0xFFFF));
                return IntPtr.Zero;

            case Win32.WM_CLOSE:
                Win32.DestroyWindow(_hwnd);
                return IntPtr.Zero;

            case Win32.WM_DESTROY:
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private short GetXL(IntPtr lParam) => (short)(lParam.ToInt32() & 0xFFFF);
    private short GetYL(IntPtr lParam) => (short)((lParam.ToInt32() >> 16) & 0xFFFF);

    private void OnLButtonDown(IntPtr wParam, IntPtr lParam)
    {
        if (_clickThrough || _locked) return;
        int x = GetXL(lParam);
        int y = GetYL(lParam);

        // Check control button hits
        if (!_locked)
        {
            foreach (var btn in _controlButtons)
            {
                if (x >= btn.X && x < btn.X + btn.W && y >= btn.Y && y < btn.Y + btn.H)
                {
                    HandleControlClick(btn.Command);
                    return;
                }
            }

            // Check slider hit
            if (_mediaDuration > 0 && x >= _sliderX && x < _sliderX + _sliderW && y >= _sliderY - 8 && y < _sliderY + _sliderH + 8)
            {
                _sliderDragging = true;
                UpdateSliderFromMouse(x);
                Win32.SetCapture(_hwnd);
                return;
            }
        }

        // Resize zone (bottom-right corner)
        Win32.GetClientRect(_hwnd, out var rect);
        int cw = rect.Right - rect.Left;
        int ch = rect.Bottom - rect.Top;
        if (x >= cw - 26 && y >= ch - 26)
        {
            _dragMode = "resize";
        }
        else
        {
            _dragMode = "move";
        }

        Win32.GetWindowRect(_hwnd, out var wRect);
        _dragStartX = (short)(Win32.GetMessagePos() & 0xFFFF);
        _dragStartY = (short)((Win32.GetMessagePos() >> 16) & 0xFFFF);
        _dragStartLeft = wRect.Left;
        _dragStartTop = wRect.Top;
        _dragStartWidth = wRect.Right - wRect.Left;
        _dragStartHeight = wRect.Bottom - wRect.Top;

        Win32.SetCapture(_hwnd);
    }

    private void OnLButtonUp(IntPtr wParam, IntPtr lParam)
    {
        if (_sliderDragging)
        {
            _sliderDragging = false;
            // Send seek command
            if (_mediaDuration > 0)
            {
                int x = GetXL(lParam);
                float ratio = (float)(x - _sliderX) / _sliderW;
                ratio = Math.Max(0, Math.Min(1, ratio));
                double seekTime = _mediaDuration * ratio;
                _mediaPosition = seekTime;
                OnMediaSeek?.Invoke(seekTime);
            }
            Win32.ReleaseCapture();
            RequestRepaint();
            return;
        }

        if (_dragMode != null)
        {
            // After manual move/resize, set local geometry override
            // so remote geometry from subtitle payloads doesn't snap us back
            if (_dragMode == "move" || _dragMode == "resize")
                _localGeometryOverride = true;
            _dragMode = null;
            Win32.ReleaseCapture();
        }
    }

    private void OnMouseMove(IntPtr wParam, IntPtr lParam)
    {
        if (_clickThrough || _locked) return;
        int x = GetXL(lParam);
        int y = GetYL(lParam);

        Win32.GetClientRect(_hwnd, out var rect);
        int cw = rect.Right - rect.Left;
        int ch = rect.Bottom - rect.Top;

        // Update hover state
        bool inResize = x >= cw - 26 && y >= ch - 26;
        if (inResize != _hoveringResize)
        {
            _hoveringResize = inResize;
            Win32.SetCursor(Win32.LoadCursorW(IntPtr.Zero, inResize ? Win32.IDC_SIZENWSE : Win32.IDC_ARROW));
        }

        // Slider hover
        bool inSlider = x >= _sliderX && x < _sliderX + _sliderW && y >= _sliderY && y < _sliderY + _sliderH;
        _hoveringSlider = inSlider;

        if (_sliderDragging)
        {
            UpdateSliderFromMouse(x);
            return;
        }

        if (_dragMode == "resize")
        {
            Win32.GetCursorPos(out var screenPt);
            int dx = screenPt.X - _dragStartX;
            int dy = screenPt.Y - _dragStartY;
            int newW = Math.Max(280, _dragStartWidth + dx);
            int newH = Math.Max(80, _dragStartHeight + dy);
            Win32.MoveWindow(_hwnd, _dragStartLeft, _dragStartTop, newW, newH, true);
            // Size changed — full redraw (recreates DIB if needed)
            RequestRepaint();
        }
        else if (_dragMode == "move")
        {
            // Move only — no redraw needed, just reposition and re-present existing DIB
            Win32.GetCursorPos(out var screenPt);
            int newX = screenPt.X - (_dragStartX - _dragStartLeft);
            int newY = screenPt.Y - (_dragStartY - _dragStartTop);
            Win32.MoveWindow(_hwnd, newX, newY, _dragStartWidth, _dragStartHeight, true);
            // Just re-present the existing DIB at the new position (no BeginDraw/EndDraw)
            if (_useUlw && _memDC != IntPtr.Zero)
                UpdateLayeredWindowPresent(_dragStartWidth, _dragStartHeight);
        }
    }

    private void UpdateSliderFromMouse(int x)
    {
        if (_mediaDuration <= 0) return;
        float ratio = (float)(x - _sliderX) / _sliderW;
        ratio = Math.Max(0, Math.Min(1, ratio));
        _mediaPosition = _mediaDuration * ratio;
        RequestRepaint();
    }

    private void HandleControlClick(string command)
    {
        switch (command)
        {
            case "fontBigger":
                StepFontSize(2);
                break;
            case "fontSmaller":
                StepFontSize(-2);
                break;
            case "previousTrack":
                OnMediaCommand?.Invoke("previousTrack");
                break;
            case "playPause":
                OnMediaCommand?.Invoke("playPause");
                break;
            case "nextTrack":
                OnMediaCommand?.Invoke("nextTrack");
                break;
            case "style":
                _stylePanelVisible = !_stylePanelVisible;
                RequestRepaint();
                break;
            case "textColor":
                ShowColorPicker("textColor");
                break;
            case "outlineColor":
                ShowColorPicker("outlineColor");
                break;
            case "controlColor":
                ShowColorPickerControl("controlColor");
                break;
            case "controlBackground":
                ShowColorPickerControl("controlBackground");
                break;
            case "controlBackgroundOpacityDown":
                StepControlBackgroundOpacity(-10);
                break;
            case "controlBackgroundOpacityUp":
                StepControlBackgroundOpacity(10);
                break;
            case "clear":
                ClearSubtitle();
                break;
            case "lock":
                SetLocked(true);
                break;
        }
    }

    private void StepFontSize(int delta)
    {
        int fs = GetStyleInt("fontSize", 34);
        int tfs = GetStyleInt("translationFontSize", 24);
        var style = new JsonObject
        {
            ["fontSize"] = Math.Max(12, Math.Min(120, fs + delta)),
            ["translationFontSize"] = Math.Max(10, Math.Min(96, tfs + delta)),
        };
        ApplyStyle(style, true);
    }

    private void CycleStyleColor(string key)
    {
        string current = GetStyleString(key, ColorPalette[0]);
        var style = new JsonObject { [key] = NextPaletteColor(current) };
        ApplyStyle(style, true);
    }

    private void CycleControlColor()
    {
        _controlColorHex = NextPaletteColor(_controlColorHex);
        OnStyleChanged?.Invoke(_style);
        RequestRepaint();
    }

    private void CycleControlBackgroundColor()
    {
        _controlBackgroundColorHex = NextPaletteColor(_controlBackgroundColorHex);
        OnStyleChanged?.Invoke(_style);
        RequestRepaint();
    }

    private void StepControlBackgroundOpacity(int delta)
    {
        _controlBackgroundOpacityPercent = Math.Max(0, Math.Min(100, _controlBackgroundOpacityPercent + delta));
        OnStyleChanged?.Invoke(_style);
        RequestRepaint();
    }

    // --- Color picker ---

    private void ShowColorPicker(string styleKey)
    {
        string currentHex = GetStyleString(styleKey, "#ffffff");
        int currentOpacity = GetStyleInt(styleKey + "Opacity", 100);

        // Parse current color to COLORREF (BGR format for Windows)
        ParseColor(currentHex, 100, out float r, out float g, out float b, out _);
        int colorRef = ((int)(b * 255) << 16) | ((int)(g * 255) << 8) | (int)(r * 255);

        var cc = new Win32.CHOOSECOLORW
        {
            lStructSize = Marshal.SizeOf<Win32.CHOOSECOLORW>(),
            hwndOwner = _hwnd,
            rgbResult = colorRef,
            lpCustColors = Marshal.AllocHGlobal(16 * 4),
            Flags = Win32.CC_RGBINIT | Win32.CC_FULLOPEN,
        };

        // Copy custom colors to unmanaged memory
        Marshal.Copy(_custColors, 0, cc.lpCustColors, 16);

        bool ok = Win32.ChooseColorW(ref cc);

        // Copy back
        Marshal.Copy(cc.lpCustColors, _custColors, 0, 16);
        Marshal.FreeHGlobal(cc.lpCustColors);

        if (ok)
        {
            int cr = cc.rgbResult;
            string newHex = $"#{(cr & 0xFF):x2}{((cr >> 8) & 0xFF):x2}{((cr >> 16) & 0xFF):x2}";
            var style = new JsonObject { [styleKey] = newHex };
            ApplyStyle(style, true);
        }
    }

    private void ShowColorPickerControl(string controlKey)
    {
        string currentHex;
        if (controlKey == "controlColor")
            currentHex = _controlColorHex;
        else
            currentHex = _controlBackgroundColorHex;

        ParseColor(currentHex, 100, out float r, out float g, out float b, out _);
        int colorRef = ((int)(b * 255) << 16) | ((int)(g * 255) << 8) | (int)(r * 255);

        var cc = new Win32.CHOOSECOLORW
        {
            lStructSize = Marshal.SizeOf<Win32.CHOOSECOLORW>(),
            hwndOwner = _hwnd,
            rgbResult = colorRef,
            lpCustColors = Marshal.AllocHGlobal(16 * 4),
            Flags = Win32.CC_RGBINIT | Win32.CC_FULLOPEN,
        };
        Marshal.Copy(_custColors, 0, cc.lpCustColors, 16);

        bool ok = Win32.ChooseColorW(ref cc);

        Marshal.Copy(cc.lpCustColors, _custColors, 0, 16);
        Marshal.FreeHGlobal(cc.lpCustColors);

        if (ok)
        {
            int cr = cc.rgbResult;
            string newHex = $"#{(cr & 0xFF):x2}{((cr >> 8) & 0xFF):x2}{((cr >> 16) & 0xFF):x2}";
            if (controlKey == "controlColor")
                _controlColorHex = newHex;
            else
                _controlBackgroundColorHex = newHex;
            OnStyleChanged?.Invoke(_style);
            RequestRepaint();
        }
    }

    private static string NextPaletteColor(string current)
    {
        string normalized = NormalizeColorKey(current);
        int index = 0;
        for (int i = 0; i < ColorPalette.Length; i++)
        {
            if (NormalizeColorKey(ColorPalette[i]) == normalized)
            {
                index = (i + 1) % ColorPalette.Length;
                break;
            }
        }
        return ColorPalette[index];
    }

    private static string NormalizeColorKey(string color)
    {
        string hex = color.Trim().TrimStart('#');
        if (hex.Length >= 6)
            return hex.Substring(0, 6).ToLowerInvariant();
        return hex.ToLowerInvariant();
    }

    private int GetStyleInt(string key, int defaultValue)
    {
        if (_style.TryGetValue(key, out var v) && v is JsonNumber n) return n.ToInt();
        return defaultValue;
    }

    private string GetStyleString(string key, string defaultValue)
    {
        if (_style.TryGetValue(key, out var v)) return v.ToString();
        return defaultValue;
    }

    // --- Tray ---
    private IntPtr _trayIcon;
    private bool _trayCreated;

    public void CreateTrayIcon(string url)
    {
        _trayIcon = Win32.CreateTrayIcon();
        var nid = new Win32.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Win32.NIF_ICON | Win32.NIF_MESSAGE | Win32.NIF_TIP,
            uCallbackMessage = (int)Win32.WM_TRAYICON,
            hIcon = _trayIcon,
            szTip = $"YuraSub {url}",
        };
        Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref nid);
        _trayCreated = true;
    }

    public void UpdateTrayTooltip(string url, int clientCount)
    {
        if (!_trayCreated) return;
        string tip = clientCount > 0 ? $"YuraSub {url} | clients: {clientCount}" : $"YuraSub {url}";
        var nid = new Win32.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Win32.NIF_TIP,
            szTip = tip,
        };
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref nid);
    }

    private void OnTrayMessage(IntPtr lParam)
    {
        int msg = lParam.ToInt32();
        if (msg == Win32.WM_LBUTTONUP_TRAY)
        {
            ShowAndActivate();
        }
        else if (msg == Win32.WM_RBUTTONUP_TRAY)
        {
            ShowTrayMenu();
        }
    }

    /// <summary>
    /// Unlock, show, raise, and ensure the window is on a visible monitor.
    /// Called from tray left-click and "Show Window" menu.
    /// </summary>
    public void ShowAndActivate()
    {
        UnlockForEditing();
        // Ensure window is on a visible monitor
        Win32.GetWindowRect(_hwnd, out var rect);
        int w = Math.Max(280, rect.Right - rect.Left);
        int h = Math.Max(80, rect.Bottom - rect.Top);
        int x = rect.Left, y = rect.Top;
        ClampToMonitor(ref x, ref y, w, h);
        if (x != rect.Left || y != rect.Top)
            Win32.MoveWindow(_hwnd, x, y, w, h, true);
        Win32.SetForegroundWindow(_hwnd);
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_SHOWWINDOW | Win32.SWP_NOACTIVATE);
        RequestRepaint();
    }

    private void ShowTrayMenu()
    {
        Win32.SetForegroundWindow(_hwnd);
        IntPtr menu = Win32.CreatePopupMenu();

        // Unlock/interactive
        string interactiveLabel = "解锁拖动/显示控制";
        bool alreadyInteractive = !_locked && !_clickThrough;
        Win32.AppendMenuW(menu, alreadyInteractive ? 0x00000008u : 0x00000000u, (IntPtr)TRAY_ID_TOGGLE_INTERACTIVE, interactiveLabel);

        // Lock
        Win32.AppendMenuW(menu, _locked ? 0x00000008u : 0x00000000u, (IntPtr)TRAY_ID_LOCK, "锁定字幕");

        // Clear
        Win32.AppendMenuW(menu, 0x00000000, (IntPtr)TRAY_ID_CLEAR, "清空字幕");

        // Show
        Win32.AppendMenuW(menu, 0x00000000, (IntPtr)TRAY_ID_SHOW, "显示窗口");

        // Restore defaults
        Win32.AppendMenuW(menu, 0x00000000, (IntPtr)TRAY_ID_RESTORE_DEFAULTS, "恢复默认设置");

        // Separator
        Win32.AppendMenuW(menu, 0x00000800, IntPtr.Zero, "");

        // Quit
        Win32.AppendMenuW(menu, 0x00000000, (IntPtr)TRAY_ID_QUIT, "退出");

        Win32.GetCursorPos(out var pt);
        int command = Win32.TrackPopupMenu(menu, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        Win32.DestroyMenu(menu);
        if (command != 0)
            OnTrayCommand(command);
    }

    private void OnTrayCommand(int id)
    {
        switch (id)
        {
            case TRAY_ID_TOGGLE_INTERACTIVE:
                UnlockForEditing();
                break;
            case TRAY_ID_LOCK:
                SetLocked(true);
                break;
            case TRAY_ID_CLEAR:
                ClearSubtitle();
                break;
            case TRAY_ID_SHOW:
                ShowAndActivate();
                break;
            case TRAY_ID_RESTORE_DEFAULTS:
                ResetToDefaults();
                break;
            case TRAY_ID_QUIT:
                Win32.PostMessageW(_hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                break;
        }
    }

    // --- Rendering (DC render target + UpdateLayeredWindow) ---

    private int _renderFrameCount;
    private int _lastAlphaNonZero;

    /// <summary>
    /// Fatal fallback: switch from ULW to HWND render target (black background).
    /// This is a last-resort recovery, not a normal code path.
    /// </summary>
    private void FatalFallbackToHwndRt(string reason)
    {
        if (!_useUlw) return; // Already in fallback mode
        _useUlw = false;
        LogToFile($"FATAL FALLBACK to HWND RT (black bg): {reason}");
        OnLog?.Invoke($"Fatal fallback to HWND RT: {reason}");

        // Release ULW resources
        ReleaseRenderSurface();

        // Create HWND render target
        Win32.GetClientRect(_hwnd, out var rect);
        int w = Math.Max(1, rect.Right - rect.Left);
        int h = Math.Max(1, rect.Bottom - rect.Top);
        try
        {
            _renderTarget = D2DFactory.CreateHwndRenderTarget(_d2dFactory, _hwnd, w, h);
            _renderTargetWidth = w;
            _renderTargetHeight = h;
            D2DFactory.SetTextAntialiasMode(_renderTarget, 1);
            LogToFile($"HWND render target created: {w}x{h}");

            // Re-apply layered window attributes for HWND RT path
            Win32.SetLayeredWindowAttributes(_hwnd, 0, 255, 0x02); // LWA_ALPHA

            // Trigger a repaint via WM_PAINT
            Win32.InvalidateRect(_hwnd, IntPtr.Zero, false);
            Win32.UpdateWindow(_hwnd);
        }
        catch (Exception ex)
        {
            LogToFile($"HWND RT fallback ALSO FAILED: {ex.Message}");
            ShowError($"YuraSub rendering failed:\n{ex.Message}\n\nBoth ULW and HWND RT paths failed.");
        }
    }

    /// <summary>
    /// Attempt to recover ULW transparent rendering after a fatal fallback.
    /// </summary>
    public void RecoverUlwTransparent()
    {
        if (_useUlw) return; // Already in ULW mode
        LogToFile("Attempting ULW transparent recovery...");

        // Release HWND render target
        if (_renderTarget != IntPtr.Zero) { D2DFactory.Release(_renderTarget); _renderTarget = IntPtr.Zero; }

        try
        {
            _useUlw = true;
            _ulwZeroAlphaCount = 0;
            _renderRetryCount = 0;
            RecreateRenderSurface();
            LogToFile("ULW transparent recovery successful");
            RenderFrame();
        }
        catch (Exception ex)
        {
            LogToFile($"ULW recovery failed: {ex.Message}");
            _useUlw = false;
            FatalFallbackToHwndRt($"ULW recovery failed: {ex.Message}");
        }
    }

    /// <summary>
    /// OnPaint handler for HWND render target fallback path.
    /// Renders directly to the HWND render target (black background, but visible).
    /// </summary>
    private void OnPaint()
    {
        if (!_d2dInitialized || _renderTarget == IntPtr.Zero)
        {
            Win32.ValidateRect(_hwnd, IntPtr.Zero);
            return;
        }

        Win32.GetClientRect(_hwnd, out var rect);
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;

        // Resize render target if needed
        if (_renderTargetWidth != w || _renderTargetHeight != h)
        {
            D2DFactory.Resize(_renderTarget, new D2D1_SIZE_U { Width = (uint)w, Height = (uint)h });
            _renderTargetWidth = w;
            _renderTargetHeight = h;
        }

        _drawRt = _renderTarget;
        D2DFactory.BeginDraw(_drawRt);
        D2DFactory.Clear(_drawRt, new D2D1_COLOR_F { R = 0, G = 0, B = 0, A = 1.0f }); // Black background

        // Draw all content — uses _drawRt which is set to _renderTarget
        DrawToolbarAndContent(w, h);

        D2DFactory.EndDraw(_drawRt);
        Win32.ValidateRect(_hwnd, IntPtr.Zero);
    }

    /// <summary>
    /// Draw all toolbar, subtitle, border, and grip content.
    /// Uses _drawRt which must be set before calling.
    /// Shared by both ULW (RenderFrame) and HWND RT (OnPaint) paths.
    /// </summary>
    private void DrawToolbarAndContent(int w, int h)
    {
        // Build control buttons
        _controlButtons.Clear();
        if (!_locked)
        {
            float toolbarY = 8;
            float toolbarH = 42;
            float btnY = toolbarY + 4;
            float btnH = 34;
            float gap = 18;
            float sliderDesired = Math.Max(260, Math.Min(780, w * 0.42f));
            float fixedWidth = 40 + 40 + 44 + 96 + 44 + 40 + 36 + 36 + 36 + gap * 8;
            float sliderW = Math.Max(120, Math.Min(sliderDesired, Math.Max(120, w - 56 - fixedWidth)));
            float groupW = fixedWidth + sliderW;
            float btnX = Math.Max(22, (w - groupW) / 2);

            AddButton("A+", "增大字号", "fontBigger", ref btnX, btnY, 40, btnH, gap);
            AddButton("A-", "减小字号", "fontSmaller", ref btnX, btnY, 40, btnH, gap);
            AddButton("", "上一首", "previousTrack", ref btnX, btnY, 44, btnH, gap);

            // Slider
            _sliderX = btnX;
            _sliderY = btnY + 8;
            _sliderW = sliderW;
            _sliderH = btnH - 16;
            btnX += _sliderW + gap;

            // Time label
            string timeText = FormatTime(_mediaPosition) + " / " + FormatTime(_mediaDuration);
            float timeX = btnX;
            btnX += 96 + gap;

            AddButton("", "下一首", "nextTrack", ref btnX, btnY, 44, btnH, gap);
            AddButton("", "播放/暂停", "playPause", ref btnX, btnY, 40, btnH, gap);
            AddButton("◐", "样式", "style", ref btnX, btnY, 36, btnH, gap);
            AddButton("×", "清空字幕", "clear", ref btnX, btnY, 36, btnH, gap);
            AddButton("", "锁定并点击穿透", "lock", ref btnX, btnY, 36, btnH, gap);

            // Draw controls background
            ParseColor(_controlBackgroundColorHex, _controlBackgroundOpacityPercent, out float bgR, out float bgG, out float bgB, out float bgA);
            var controlBg = new D2D1_ROUNDED_RECT
            {
                Rect = new D2D1_RECT_F { Left = 8, Top = toolbarY, Right = w - 8, Bottom = toolbarY + toolbarH },
                RadiusX = 12,
                RadiusY = 12,
            };
            var bgBrush = D2DFactory.CreateSolidColorBrush(_drawRt, new D2D1_COLOR_F { R = bgR, G = bgG, B = bgB, A = bgA });
            D2DFactory.FillRoundedRect(_drawRt, controlBg, bgBrush);
            D2DFactory.Release(bgBrush);

            // Draw border
            var borderBrush = D2DFactory.CreateSolidColorBrush(_drawRt, new D2D1_COLOR_F { R = 229f/255, G = 241f/255, B = 232f/255, A = 0.07f });
            D2DFactory.DrawRoundedRect(_drawRt, controlBg, borderBrush, 1, IntPtr.Zero);
            D2DFactory.Release(borderBrush);

            // Parse control color
            ParseColor(_controlColorHex, _controlOpacityPercent, out float ccR, out float ccG, out float ccB, out float ccA);
            var controlColor = new D2D1_COLOR_F { R = ccR, G = ccG, B = ccB, A = ccA };

            // Draw vector buttons
            var btnBrush = D2DFactory.CreateSolidColorBrush(_drawRt, controlColor);
            foreach (var btn in _controlButtons)
            {
                if (btn.Command == "previousTrack")
                    DrawPreviousButton(btn.X, btn.Y, btn.W, btn.H, btnBrush);
                else if (btn.Command == "nextTrack")
                    DrawNextButton(btn.X, btn.Y, btn.W, btn.H, btnBrush);
                else if (btn.Command == "playPause")
                    DrawPlayPauseButton(btn.X, btn.Y, btn.W, btn.H, btnBrush);
                else if (btn.Command == "lock")
                    DrawLockButton(btn.X, btn.Y, btn.W, btn.H, btnBrush);
                else if (btn.Command == "clear")
                    DrawClearButton(btn.X, btn.Y, btn.W, btn.H, btnBrush);
                else if (btn.Command == "style")
                    DrawStyleButton(btn.X, btn.Y, btn.W, btn.H, btnBrush);
                else
                    DrawText(btn.Label, btn.X + 4, btn.Y + 2, btn.W - 8, btn.H - 4, btnBrush, 20, 700);
            }

            // Draw slider
            DrawSlider(_sliderX, _sliderY, _sliderW, _sliderH, controlColor);

            // Draw time text
            DrawText(timeText, timeX, btnY + 2, 96, btnH - 4, btnBrush, 12, 400);

            D2DFactory.Release(btnBrush);

            if (_stylePanelVisible)
                DrawStylePanel(w, toolbarY + toolbarH + 4, controlColor, new D2D1_COLOR_F { R = bgR, G = bgG, B = bgB, A = bgA });
        }

        // Draw subtitle text
        if (HasVisibleText)
        {
            float textTop = _locked ? 8 : (_stylePanelVisible ? 102 : 52);
            float textAreaW = w - 36;
            float textAreaH = h - textTop - 8;

            if (textAreaW > 20 && textAreaH > 20)
            {
                // Background
                int bgOpacity = GetStyleInt("backgroundOpacity", 0);
                if (bgOpacity > 0)
                {
                    string bgHex = GetStyleString("backgroundColor", "#00000000");
                    ParseColor(bgHex, bgOpacity, out float bgR, out float bgG, out float bgB, out float bgA);
                    if (bgA > 0)
                    {
                        float radius = (float)GetStyleInt("backgroundRadius", 12);
                        var bgRect = new D2D1_ROUNDED_RECT
                        {
                            Rect = new D2D1_RECT_F { Left = 1, Top = 1, Right = w - 1, Bottom = h - 1 },
                            RadiusX = radius,
                            RadiusY = radius,
                        };
                        var bg = D2DFactory.CreateSolidColorBrush(_drawRt, new D2D1_COLOR_F { R = bgR, G = bgG, B = bgB, A = bgA });
                        D2DFactory.FillRoundedRect(_drawRt, bgRect, bg);
                        D2DFactory.Release(bg);
                    }
                }

                DrawSubtitleText(textTop, textAreaW, textAreaH);
            }
        }
        // Draw edit mode border and grip
        if (!_clickThrough)
        {
            var borderC = new D2D1_COLOR_F { R = 125f/255, G = 211f/255, B = 252f/255, A = 0.47f };
            var borderB = D2DFactory.CreateSolidColorBrush(_drawRt, borderC);
            var borderRect = new D2D1_ROUNDED_RECT
            {
                Rect = new D2D1_RECT_F { Left = 1, Top = 1, Right = w - 2, Bottom = h - 2 },
                RadiusX = 10,
                RadiusY = 10,
            };
            D2DFactory.DrawRoundedRect(_drawRt, borderRect, borderB, 1.5f, IntPtr.Zero);

            // Grip lines
            ParseColor(_controlColorHex, _controlOpacityPercent, out float gcR, out float gcG, out float gcB, out float gcA);
            var gripBrush = D2DFactory.CreateSolidColorBrush(_drawRt, new D2D1_COLOR_F { R = gcR, G = gcG, B = gcB, A = Math.Max(0, gcA - 0.02f) });
            float gx = w - 24, gy = h - 24;
            D2DFactory.DrawLine(_drawRt,
                new D2D1_POINT_2F { X = gx, Y = gy + 16 },
                new D2D1_POINT_2F { X = gx + 16, Y = gy },
                gripBrush, 2, IntPtr.Zero);
            D2DFactory.DrawLine(_drawRt,
                new D2D1_POINT_2F { X = gx + 6, Y = gy + 16 },
                new D2D1_POINT_2F { X = gx + 16, Y = gy + 6 },
                gripBrush, 2, IntPtr.Zero);
            D2DFactory.Release(gripBrush);
            D2DFactory.Release(borderB);
        }
    }

    private void RenderFrame()
    {
        RenderFrameInternal(0);
    }

    private void RenderFrameInternal(int retryDepth)
    {
        if (!_d2dInitialized || _dcRenderTarget == IntPtr.Zero) return;

        Win32.GetClientRect(_hwnd, out var clientRect);
        int w = Math.Max(1, clientRect.Right - clientRect.Left);
        int h = Math.Max(1, clientRect.Bottom - clientRect.Top);

        // Recreate DIB if size changed
        if (_dcRTWidth != w || _dcRTHeight != h)
        {
            try { RecreateRenderSurface(); }
            catch (Exception ex)
            {
                LogToFile($"RecreateRenderSurface failed: {ex.Message}");
                FatalFallbackToHwndRt($"RecreateRenderSurface failed: {ex.Message}");
                return;
            }
        }

        if (_dcRenderTarget == IntPtr.Zero || _memDC == IntPtr.Zero) return;

        _drawRt = _dcRenderTarget;
        D2DFactory.BeginDraw(_drawRt);
        D2DFactory.Clear(_drawRt, new D2D1_COLOR_F { R = 0, G = 0, B = 0, A = 0 }); // Fully transparent

        DrawToolbarAndContent(w, h);

        int endDrawHr = D2DFactory.EndDraw(_drawRt);
        if (endDrawHr != 0)
        {
            // D2DERR_RECREATE_TARGET is recoverable — recreate surface and retry
            if (endDrawHr == D2DERR_RECREATE_TARGET && retryDepth < 2)
            {
                LogToFile($"EndDraw D2DERR_RECREATE_TARGET, recreating surface (retry {retryDepth + 1})");
                try
                {
                    ReleaseRenderSurface();
                    RecreateRenderSurface();
                    RenderFrameInternal(retryDepth + 1);
                    return;
                }
                catch (Exception ex)
                {
                    LogToFile($"Recreate after D2DERR_RECREATE_TARGET failed: {ex.Message}");
                }
            }

            // Non-recoverable EndDraw error or retry exhausted
            string msg = $"EndDraw failed: 0x{endDrawHr:X8} (retry={retryDepth})";
            LogToFile(msg);
            FatalFallbackToHwndRt(msg);
            return;
        }

        // Alpha diagnostic: count non-zero alpha pixels in DIB
        _renderFrameCount++;
        _renderRetryCount = 0; // Reset retry counter on success
        int alphaNonZero = CountNonZeroAlphaPixels(w, h);
        _lastAlphaNonZero = alphaNonZero;

        if (_renderFrameCount <= 5 || _renderFrameCount % 60 == 0)
        {
            if (alphaNonZero == 0)
                LogToFile($"RenderFrame #{_renderFrameCount}: 0 non-zero alpha pixels ({w}x{h}), locked={_locked}");
            else if (_renderFrameCount <= 5)
                LogToFile($"RenderFrame #{_renderFrameCount}: {alphaNonZero} non-zero alpha pixels ({w}x{h})");
        }

        // If alpha is zero for 3+ consecutive frames, the ULW path is broken
        if (alphaNonZero == 0 && !_locked)
        {
            _ulwZeroAlphaCount++;
            if (_ulwZeroAlphaCount >= 3)
            {
                LogToFile($"ULW produced 0 alpha pixels for {_ulwZeroAlphaCount} consecutive frames");
                FatalFallbackToHwndRt("ULW produced 0 alpha pixels for 3+ consecutive frames");
                return;
            }
        }
        else
        {
            _ulwZeroAlphaCount = 0;
        }

        // Present via UpdateLayeredWindow
        UpdateLayeredWindowPresent(w, h);
    }

    /// <summary>
    /// Present the current DIB via UpdateLayeredWindow.
    /// Can be called independently (e.g., after move without redraw).
    /// </summary>
    private void UpdateLayeredWindowPresent(int w, int h)
    {
        Win32.GetWindowRect(_hwnd, out var winRect);
        var destPt = new Win32.POINT { X = winRect.Left, Y = winRect.Top };
        var srcPt = new Win32.POINT { X = 0, Y = 0 };
        var size = new Win32.SIZE { cx = w, cy = h };
        var blend = new Win32.BLENDFUNCTION
        {
            BlendOp = 0, // AC_SRC_OVER
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = Win32.AC_SRC_ALPHA,
        };
        bool ulwOk = Win32.UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref destPt, ref size, _memDC, ref srcPt, 0, ref blend, Win32.ULW_ALPHA);
        if (!ulwOk)
        {
            int err = Marshal.GetLastWin32Error();
            LogToFile($"UpdateLayeredWindow failed: error={err}, dest=({destPt.X},{destPt.Y}), size={w}x{h}");
            // Don't fallback on single ULW failure — just log and continue
        }
    }

    /// <summary>
    /// Diagnostic: count pixels with alpha > 0 in the DIB.
    /// </summary>
    private unsafe int CountNonZeroAlphaPixels(int w, int h)
    {
        if (_dibPixels == IntPtr.Zero) return -1;
        int count = 0;
        uint* pixels = (uint*)_dibPixels;
        int total = w * h;
        for (int i = 0; i < total; i++)
        {
            if ((pixels[i] & 0xFF000000) != 0) // Alpha in bits 24-31 for BGRA
                count++;
        }
        return count;
    }

    // --- Vector button drawing ---

    private void DrawPreviousButton(float x, float y, float w, float h, IntPtr brush)
    {
        float cx = x + w / 2f;
        float cy = y + h / 2f;
        float s = h * 0.32f; // Size scale
        // Triangle pointing left: (cx+s/2, cy-s) → (cx-s/2, cy) → (cx+s/2, cy+s)
        D2DFactory.DrawLine(_drawRt,
            new D2D1_POINT_2F { X = cx + s * 0.5f, Y = cy - s },
            new D2D1_POINT_2F { X = cx - s * 0.5f, Y = cy },
            brush, 2, IntPtr.Zero);
        D2DFactory.DrawLine(_drawRt,
            new D2D1_POINT_2F { X = cx - s * 0.5f, Y = cy },
            new D2D1_POINT_2F { X = cx + s * 0.5f, Y = cy + s },
            brush, 2, IntPtr.Zero);
        D2DFactory.DrawLine(_drawRt,
            new D2D1_POINT_2F { X = cx + s * 0.5f, Y = cy + s },
            new D2D1_POINT_2F { X = cx + s * 0.5f, Y = cy - s },
            brush, 2, IntPtr.Zero);
        // Vertical bar
        float barX = cx - s * 0.8f;
        D2DFactory.DrawLine(_drawRt,
            new D2D1_POINT_2F { X = barX, Y = cy - s },
            new D2D1_POINT_2F { X = barX, Y = cy + s },
            brush, 2.5f, IntPtr.Zero);
    }

    private void DrawNextButton(float x, float y, float w, float h, IntPtr brush)
    {
        float cx = x + w / 2f;
        float cy = y + h / 2f;
        float s = h * 0.32f;
        // Triangle pointing right: (cx-s/2, cy-s) → (cx+s/2, cy) → (cx-s/2, cy+s)
        D2DFactory.DrawLine(_drawRt,
            new D2D1_POINT_2F { X = cx - s * 0.5f, Y = cy - s },
            new D2D1_POINT_2F { X = cx + s * 0.5f, Y = cy },
            brush, 2, IntPtr.Zero);
        D2DFactory.DrawLine(_drawRt,
            new D2D1_POINT_2F { X = cx + s * 0.5f, Y = cy },
            new D2D1_POINT_2F { X = cx - s * 0.5f, Y = cy + s },
            brush, 2, IntPtr.Zero);
        D2DFactory.DrawLine(_drawRt,
            new D2D1_POINT_2F { X = cx - s * 0.5f, Y = cy + s },
            new D2D1_POINT_2F { X = cx - s * 0.5f, Y = cy - s },
            brush, 2, IntPtr.Zero);
        // Vertical bar
        float barX = cx + s * 0.8f;
        D2DFactory.DrawLine(_drawRt,
            new D2D1_POINT_2F { X = barX, Y = cy - s },
            new D2D1_POINT_2F { X = barX, Y = cy + s },
            brush, 2.5f, IntPtr.Zero);
    }

    private void DrawPlayPauseButton(float x, float y, float w, float h, IntPtr brush)
    {
        float cx = x + w / 2f;
        float cy = y + h / 2f;
        float s = h * 0.35f;

        if (_mediaPaused)
        {
            // Play: right-pointing triangle
            D2DFactory.DrawLine(_drawRt,
                new D2D1_POINT_2F { X = cx - s * 0.5f, Y = cy - s },
                new D2D1_POINT_2F { X = cx + s * 0.7f, Y = cy },
                brush, 2, IntPtr.Zero);
            D2DFactory.DrawLine(_drawRt,
                new D2D1_POINT_2F { X = cx + s * 0.7f, Y = cy },
                new D2D1_POINT_2F { X = cx - s * 0.5f, Y = cy + s },
                brush, 2, IntPtr.Zero);
            D2DFactory.DrawLine(_drawRt,
                new D2D1_POINT_2F { X = cx - s * 0.5f, Y = cy + s },
                new D2D1_POINT_2F { X = cx - s * 0.5f, Y = cy - s },
                brush, 2, IntPtr.Zero);
        }
        else
        {
            // Pause: two vertical bars
            float barW = s * 0.25f;
            float gap = s * 0.25f;
            D2DFactory.FillRectangle(_drawRt,
                new D2D1_RECT_F { Left = cx - gap - barW, Top = cy - s, Right = cx - gap, Bottom = cy + s },
                brush);
            D2DFactory.FillRectangle(_drawRt,
                new D2D1_RECT_F { Left = cx + gap, Top = cy - s, Right = cx + gap + barW, Bottom = cy + s },
                brush);
        }
    }

    private void DrawLockButton(float x, float y, float w, float h, IntPtr brush)
    {
        float cx = x + w / 2f;
        float cy = y + h / 2f;
        float s = h * 0.3f;

        // Padlock body: rectangle
        float bodyW = s * 1.4f;
        float bodyH = s * 1.0f;
        float bodyTop = cy;
        D2DFactory.DrawRectangle(_drawRt,
            new D2D1_RECT_F { Left = cx - bodyW / 2, Top = bodyTop, Right = cx + bodyW / 2, Bottom = bodyTop + bodyH },
            brush, 2, IntPtr.Zero);

        // Shackle: arc (approximate with lines)
        float shackleW = bodyW * 0.6f;
        float shackleH = s * 0.7f;
        float shackleTop = bodyTop - shackleH;
        D2DFactory.DrawLine(_drawRt,
            new D2D1_POINT_2F { X = cx - shackleW / 2, Y = bodyTop },
            new D2D1_POINT_2F { X = cx - shackleW / 2, Y = shackleTop + shackleW / 2 },
            brush, 2, IntPtr.Zero);
        D2DFactory.DrawLine(_drawRt,
            new D2D1_POINT_2F { X = cx - shackleW / 2, Y = shackleTop + shackleW / 2 },
            new D2D1_POINT_2F { X = cx, Y = shackleTop },
            brush, 2, IntPtr.Zero);
        D2DFactory.DrawLine(_drawRt,
            new D2D1_POINT_2F { X = cx, Y = shackleTop },
            new D2D1_POINT_2F { X = cx + shackleW / 2, Y = shackleTop + shackleW / 2 },
            brush, 2, IntPtr.Zero);
        D2DFactory.DrawLine(_drawRt,
            new D2D1_POINT_2F { X = cx + shackleW / 2, Y = shackleTop + shackleW / 2 },
            new D2D1_POINT_2F { X = cx + shackleW / 2, Y = bodyTop },
            brush, 2, IntPtr.Zero);
    }

    private void DrawClearButton(float x, float y, float w, float h, IntPtr brush)
    {
        float cx = x + w / 2f;
        float cy = y + h / 2f;
        float s = h * 0.3f;

        // X shape: two crossing diagonal lines
        D2DFactory.DrawLine(_drawRt,
            new D2D1_POINT_2F { X = cx - s, Y = cy - s },
            new D2D1_POINT_2F { X = cx + s, Y = cy + s },
            brush, 2.5f, IntPtr.Zero);
        D2DFactory.DrawLine(_drawRt,
            new D2D1_POINT_2F { X = cx + s, Y = cy - s },
            new D2D1_POINT_2F { X = cx - s, Y = cy + s },
            brush, 2.5f, IntPtr.Zero);
    }

    private void DrawStyleButton(float x, float y, float w, float h, IntPtr brush)
    {
        float cx = x + w / 2f;
        float cy = y + h / 2f;
        float r = h * 0.32f;

        // Half-circle: draw full circle outline, fill left half
        // Outer circle
        D2DFactory.DrawEllipse(_drawRt, cx, cy, r, r, brush, 2, IntPtr.Zero);
        // Vertical divider
        D2DFactory.DrawLine(_drawRt,
            new D2D1_POINT_2F { X = cx, Y = cy - r },
            new D2D1_POINT_2F { X = cx, Y = cy + r },
            brush, 1.5f, IntPtr.Zero);
    }

    private void DrawStylePanel(int w, float top, D2D1_COLOR_F controlColor, D2D1_COLOR_F controlBackground)
    {
        float panelH = 46;
        float panelLeft = 8;
        float panelRight = w - 8;

        var panelRect = new D2D1_ROUNDED_RECT
        {
            Rect = new D2D1_RECT_F { Left = panelLeft, Top = top, Right = panelRight, Bottom = top + panelH },
            RadiusX = 12,
            RadiusY = 12,
        };

        var bg = D2DFactory.CreateSolidColorBrush(_drawRt, new D2D1_COLOR_F
        {
            R = controlBackground.R,
            G = controlBackground.G,
            B = controlBackground.B,
            A = Math.Max(controlBackground.A, 30f / 255),
        });
        D2DFactory.FillRoundedRect(_drawRt, panelRect, bg);
        D2DFactory.Release(bg);

        var border = D2DFactory.CreateSolidColorBrush(_drawRt, new D2D1_COLOR_F { R = 229f / 255, G = 241f / 255, B = 232f / 255, A = 34f / 255 });
        D2DFactory.DrawRoundedRect(_drawRt, panelRect, border, 1, IntPtr.Zero);
        D2DFactory.Release(border);

        var brush = D2DFactory.CreateSolidColorBrush(_drawRt, controlColor);
        float btnY = top + 6;
        float btnH = 34;
        float gap = 12;
        float totalW = 86 + 86 + 86 + 86 + 74 + 74 + gap * 5;
        float x = Math.Max(panelLeft + 14, (w - totalW) / 2);

        AddButton("正文颜色", "选择正文颜色 (点击打开调色板)", "textColor", ref x, btnY, 86, btnH, gap);
        AddButton("描边颜色", "选择描边颜色", "outlineColor", ref x, btnY, 86, btnH, gap);
        AddButton("控件颜色", "选择控件颜色", "controlColor", ref x, btnY, 86, btnH, gap);
        AddButton("控件背景", "选择控件背景颜色", "controlBackground", ref x, btnY, 86, btnH, gap);
        AddButton("背景-", "降低控件背景不透明度", "controlBackgroundOpacityDown", ref x, btnY, 74, btnH, gap);
        AddButton("背景+", "提高控件背景不透明度", "controlBackgroundOpacityUp", ref x, btnY, 74, btnH, gap);

        for (int i = _controlButtons.Count - 6; i < _controlButtons.Count; i++)
        {
            var btn = _controlButtons[i];
            var buttonBg = new D2D1_ROUNDED_RECT
            {
                Rect = new D2D1_RECT_F { Left = btn.X, Top = btn.Y, Right = btn.X + btn.W, Bottom = btn.Y + btn.H },
                RadiusX = 8,
                RadiusY = 8,
            };
            var bgBrush = D2DFactory.CreateSolidColorBrush(_drawRt, new D2D1_COLOR_F
            {
                R = controlBackground.R,
                G = controlBackground.G,
                B = controlBackground.B,
                A = Math.Max(0.10f, Math.Min(0.82f, controlBackground.A + 0.18f)),
            });
            D2DFactory.FillRoundedRect(_drawRt, buttonBg, bgBrush);
            D2DFactory.Release(bgBrush);
            DrawText(btn.Label, btn.X + 4, btn.Y + 2, btn.W - 8, btn.H - 4, brush, 14, 600);
        }

        D2DFactory.Release(brush);
    }

    private void AddButton(string label, string tooltip, string command, ref float x, float y, float w, float h, float gap)
    {
        _controlButtons.Add(new ControlButton { Label = label, Tooltip = tooltip, Command = command, X = x, Y = y, W = w, H = h });
        x += w + gap;
    }

    private void DrawSlider(float x, float y, float w, float h, D2D1_COLOR_F color)
    {
        // Track background
        var trackBg = D2DFactory.CreateSolidColorBrush(_drawRt, new D2D1_COLOR_F { R = 245f/255, G = 255f/255, B = 248f/255, A = 0.21f });
        float trackY = y + h / 2 - 2;
        var trackRect = new D2D1_RECT_F { Left = x, Top = trackY, Right = x + w, Bottom = trackY + 4 };
        D2DFactory.FillRectangle(_drawRt, trackRect, trackBg);
        D2DFactory.Release(trackBg);

        // Progress
        float progress = _mediaDuration > 0 ? (float)(_mediaPosition / _mediaDuration) : 0;
        progress = Math.Max(0, Math.Min(1, progress));
        var progressColor = new D2D1_COLOR_F { R = color.R, G = color.G, B = color.B, A = Math.Max(0, color.A - 0.16f) };
        var progressBrush = D2DFactory.CreateSolidColorBrush(_drawRt, progressColor);
        var progressRect = new D2D1_RECT_F { Left = x, Top = trackY, Right = x + w * progress, Bottom = trackY + 4 };
        D2DFactory.FillRectangle(_drawRt, progressRect, progressBrush);
        D2DFactory.Release(progressBrush);

        // Handle
        float handleX = x + w * progress - 6;
        float handleY = y + h / 2 - 6;
        var handleBrush = D2DFactory.CreateSolidColorBrush(_drawRt, new D2D1_COLOR_F { R = color.R, G = color.G, B = color.B, A = Math.Max(0, color.A - 0.02f) });
        var handleRect = new D2D1_RECT_F { Left = handleX, Top = handleY, Right = handleX + 12, Bottom = handleY + 12 };
        D2DFactory.FillRectangle(_drawRt, handleRect, handleBrush);
        D2DFactory.Release(handleBrush);
    }

    private void DrawSubtitleText(float top, float areaW, float areaH)
    {
        string fontFamily = GetStyleString("fontFamily", Config.Defaults.FontFamily);
        int mainFontSize = GetStyleInt("fontSize", Config.Defaults.FontSize);
        int transFontSize = GetStyleInt("translationFontSize", Config.Defaults.TranslationFontSize);
        int maxLines = GetStyleInt("maxLines", 4);

        // Parse colors
        ParseColor(GetStyleString("textColor", Config.Defaults.TextColor), GetStyleInt("textOpacity", 100),
            out float tR, out float tG, out float tB, out float tA);
        ParseColor(GetStyleString("translationColor", "#bfefff"), GetStyleInt("translationOpacity", 100),
            out float trR, out float trG, out float trB, out float trA);
        ParseColor(GetStyleString("outlineColor", Config.Defaults.OutlineColor), GetStyleInt("outlineOpacity", 100),
            out float oR, out float oG, out float oB, out float oA);
        ParseColor(GetStyleString("shadowColor", "#000000a0"), GetStyleInt("shadowOpacity", 65),
            out float sR, out float sG, out float sB, out float sA);

        float outlineWidth = GetStyleInt("outlineWidth", Config.Defaults.OutlineWidth);
        float shadowOffX = GetStyleInt("shadowOffsetX", 2);
        float shadowOffY = GetStyleInt("shadowOffsetY", 3);
        float lineGap = GetStyleInt("lineGap", 6);
        float paddingX = GetStyleInt("paddingX", 18);
        float paddingY = GetStyleInt("paddingY", 12);

        float drawX = 18 + paddingX;
        float drawY = top + paddingY;
        float drawW = areaW - 2 * paddingX;

        // Create text format for main text
        IntPtr mainFormat = DWriteInterop.CreateTextFormat(_dwriteFactory, fontFamily, mainFontSize,
            DWriteInterop.DWRITE_FONT_WEIGHT_BOLD, DWriteInterop.DWRITE_FONT_STYLE_NORMAL, DWriteInterop.DWRITE_FONT_STRETCH_NORMAL);
        DWriteInterop.SetTextAlignment(mainFormat, 2); // CENTER
        DWriteInterop.SetParagraphAlignment(mainFormat, 0); // NEAR
        DWriteInterop.SetWordWrapping(mainFormat, DWriteInterop.DWRITE_WORD_WRAPPING_WRAP);

        // Create text format for translation
        IntPtr transFormat = DWriteInterop.CreateTextFormat(_dwriteFactory, fontFamily, transFontSize,
            DWriteInterop.DWRITE_FONT_WEIGHT_MEDIUM, DWriteInterop.DWRITE_FONT_STYLE_NORMAL, DWriteInterop.DWRITE_FONT_STRETCH_NORMAL);
        DWriteInterop.SetTextAlignment(transFormat, 2); // CENTER
        DWriteInterop.SetParagraphAlignment(transFormat, 0); // NEAR
        DWriteInterop.SetWordWrapping(transFormat, DWriteInterop.DWRITE_WORD_WRAPPING_WRAP);

        // Build layout: main text lines, then translation lines
        var lines = new List<(string text, bool isTranslation)>();
        if (!string.IsNullOrEmpty(_text))
        {
            string[] mainLines = _text.Split('\n');
            foreach (string line in mainLines)
            {
                if (lines.Count >= maxLines) break;
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add((line, false));
            }
        }
        if (!string.IsNullOrEmpty(_translation))
        {
            string[] transLines = _translation.Split('\n');
            foreach (string line in transLines)
            {
                if (lines.Count >= maxLines) break;
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add((line, true));
            }
        }

        // Draw each line
        float currentY = drawY;
        foreach (var (text, isTranslation) in lines)
        {
            IntPtr format = isTranslation ? transFormat : mainFormat;

            // Create text layout
            IntPtr layout = DWriteInterop.CreateTextLayout(_dwriteFactory, text, format, drawW, 200);
            var metrics = DWriteInterop.GetMetrics(layout);
            float lineH = metrics.Height;

            float lineX = drawX;

            // Draw shadow
            if (sA > 0)
            {
                var shadowBrush = D2DFactory.CreateSolidColorBrush(_drawRt, new D2D1_COLOR_F { R = sR, G = sG, B = sB, A = sA });
                var shadowTransform = Matrix3x2.CreateTranslation(shadowOffX, shadowOffY);
                D2DFactory.SetTransform(_drawRt, ref shadowTransform);
                D2DFactory.DrawTextLayout(_drawRt, lineX, currentY, layout, shadowBrush, 0);
                var identity = Matrix3x2.Identity;
                D2DFactory.SetTransform(_drawRt, ref identity);
                D2DFactory.Release(shadowBrush);
            }

            // Draw outline (simulate with multiple offset draws)
            if (outlineWidth > 0 && oA > 0)
            {
                var outlineBrush = D2DFactory.CreateSolidColorBrush(_drawRt, new D2D1_COLOR_F { R = oR, G = oG, B = oB, A = oA });
                float ow = outlineWidth;
                for (float dx = -ow; dx <= ow; dx += 1)
                {
                    for (float dy = -ow; dy <= ow; dy += 1)
                    {
                        if (dx * dx + dy * dy <= ow * ow)
                        {
                            var offsetTransform = Matrix3x2.CreateTranslation(dx, dy);
                            D2DFactory.SetTransform(_drawRt, ref offsetTransform);
                            D2DFactory.DrawTextLayout(_drawRt, lineX, currentY, layout, outlineBrush, 0);
                        }
                    }
                }
                var resetTransform = Matrix3x2.Identity;
                D2DFactory.SetTransform(_drawRt, ref resetTransform);
                D2DFactory.Release(outlineBrush);
            }

            // Draw main text
            var textBrush = D2DFactory.CreateSolidColorBrush(_drawRt, new D2D1_COLOR_F
            {
                R = isTranslation ? trR : tR,
                G = isTranslation ? trG : tG,
                B = isTranslation ? trB : tB,
                A = isTranslation ? trA : tA,
            });
            D2DFactory.DrawTextLayout(_drawRt, lineX, currentY, layout, textBrush, 0);
            D2DFactory.Release(textBrush);

            D2DFactory.Release(layout);
            currentY += lineH + lineGap;
        }

        D2DFactory.Release(mainFormat);
        D2DFactory.Release(transFormat);
    }

    private void DrawText(string text, float x, float y, float w, float h, IntPtr brush, float fontSize, int weight)
    {
        if (string.IsNullOrEmpty(text)) return;
        string fontFamily = GetStyleString("fontFamily", Config.Defaults.FontFamily);
        IntPtr format = DWriteInterop.CreateTextFormat(_dwriteFactory, fontFamily, fontSize, weight,
            DWriteInterop.DWRITE_FONT_STYLE_NORMAL, DWriteInterop.DWRITE_FONT_STRETCH_NORMAL);
        DWriteInterop.SetTextAlignment(format, 2); // CENTER
        DWriteInterop.SetParagraphAlignment(format, 2); // CENTER
        IntPtr layout = DWriteInterop.CreateTextLayout(_dwriteFactory, text, format, w, h);
        D2DFactory.DrawTextLayout(_drawRt, x, y, layout, brush, 0);
        D2DFactory.Release(layout);
        D2DFactory.Release(format);
    }

    private static void ParseColor(string hex, int opacityPercent, out float r, out float g, out float b, out float a)
    {
        hex = hex.TrimStart('#');
        r = g = b = 0;
        a = 1;

        if (hex.Length >= 6)
        {
            r = Convert.ToInt32(hex.Substring(0, 2), 16) / 255f;
            g = Convert.ToInt32(hex.Substring(2, 2), 16) / 255f;
            b = Convert.ToInt32(hex.Substring(4, 2), 16) / 255f;
        }
        if (hex.Length >= 8)
        {
            a = Convert.ToInt32(hex.Substring(6, 2), 16) / 255f;
        }
        a *= opacityPercent / 100f;
    }

    private static string FormatTime(double seconds)
    {
        int total = Math.Max(0, (int)seconds);
        int h = total / 3600;
        int m = (total % 3600) / 60;
        int s = total % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }

    // --- Monitor helpers ---

    /// <summary>
    /// Get work-area rect for the primary monitor.
    /// Uses MonitorFromPoint(0,0, MONITOR_DEFAULTTOPRIMARY) for reliable primary detection.
    /// </summary>
    private static Win32.RECT GetPrimaryMonitorWorkArea()
    {
        // (0,0) is always on the primary monitor in virtual screen coordinates
        IntPtr hMon = Win32.MonitorFromPoint(new Win32.POINT { X = 0, Y = 0 }, Win32.MONITOR_DEFAULTTOPRIMARY);
        if (hMon != IntPtr.Zero)
        {
            var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            if (Win32.GetMonitorInfoW(hMon, ref mi))
                return mi.rcWork;
        }
        // Fallback
        return new Win32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    /// <summary>
    /// Get work-area rects for all monitors.
    /// </summary>
    private static List<Win32.RECT> GetAllMonitorWorkAreas()
    {
        var areas = new List<Win32.RECT>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdc, ref Win32.RECT rc, IntPtr data) =>
            {
                var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
                if (Win32.GetMonitorInfoW(hMon, ref mi))
                    areas.Add(mi.rcWork);
                return true;
            }, IntPtr.Zero);
        return areas;
    }

    /// <summary>
    /// Clamp a window position so at least 200×80 pixels are visible on some monitor.
    /// If position is int.MinValue or off-screen, place on primary monitor.
    /// </summary>
    private static void ClampToMonitor(ref int x, ref int y, int w, int h)
    {
        var areas = GetAllMonitorWorkAreas();
        if (areas.Count == 0)
        {
            var primary = GetPrimaryMonitorWorkArea();
            x = primary.Left + (primary.Right - primary.Left - w) / 2;
            y = primary.Bottom - h - 80;
            return;
        }

        // Check if the window has sufficient overlap with any monitor
        bool IsVisible(int wx, int wy)
        {
            foreach (var a in areas)
            {
                int ix = Math.Max(0, Math.Min(wx + w, a.Right) - Math.Max(wx, a.Left));
                int iy = Math.Max(0, Math.Min(wy + h, a.Bottom) - Math.Max(wy, a.Top));
                if (ix >= 200 && iy >= 80) return true;
            }
            return false;
        }

        if (x != int.MinValue && y != int.MinValue && IsVisible(x, y))
            return; // Position is valid

        // Use primary monitor (not largest)
        var primaryArea = GetPrimaryMonitorWorkArea();
        x = primaryArea.Left + (primaryArea.Right - primaryArea.Left - w) / 2;
        y = primaryArea.Bottom - h - 80;
    }

    public void Dispose()
    {
        if (_trayCreated)
        {
            var nid = new Win32.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
            };
            Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref nid);
        }

        ReleaseRenderSurface();
        if (_dcRenderTarget != IntPtr.Zero) D2DFactory.Release(_dcRenderTarget);
        if (_renderTarget != IntPtr.Zero) D2DFactory.Release(_renderTarget);
        if (_d2dFactory != IntPtr.Zero) D2DFactory.Release(_d2dFactory);
        if (_dwriteFactory != IntPtr.Zero) D2DFactory.Release(_dwriteFactory);

        if (_hwnd != IntPtr.Zero)
            Win32.DestroyWindow(_hwnd);
    }
}
