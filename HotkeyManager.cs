using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SttApp;

/// <summary>
/// Registruje globální klávesovou zkratku přes Win32 RegisterHotKey / UnregisterHotKey.
/// WM_HOTKEY zprávy jsou zachytávány přes NativeWindow připnuté k MessageLoop.
/// V režimu Push-to-Talk detekuje i uvolnění klávesy (key-up) přes polling.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    // Win32 konstanty
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 9001;
    // MOD_NOREPEAT (0x4000) – zabrani opakovani pri drzeni klavesy
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public event EventHandler? HotkeyPressed;
    public event EventHandler? HotkeyReleased;

    private readonly HotkeyWindow _window;
    private bool _registered;

    // PTT key-up polling
    private System.Threading.Timer? _pttPollTimer;
    private int _pttVirtualKey;
    private int _pttModifiers;
    private volatile bool _pttKeyDown;

    public HotkeyManager()
    {
        _window = new HotkeyWindow();
        _window.HotkeyPressed += OnHotkeyPressed;
    }

    /// <summary>
    /// Zapne detekci uvolnění klávesy pro Push-to-Talk režim.
    /// Volat PŘED Register().
    /// </summary>
    public bool PushToTalkEnabled { get; set; }

    public bool Register(int modifiers, int virtualKey)
    {
        if (_registered)
            Unregister();

        _pttModifiers = modifiers;
        _pttVirtualKey = virtualKey;

        // Pridame MOD_NOREPEAT aby se nezacaly hromadit eventy pri drzeni klavesy
        _registered = RegisterHotKey(_window.Handle, HOTKEY_ID,
            (uint)modifiers | MOD_NOREPEAT, (uint)virtualKey);
        return _registered;
    }

    public void Unregister()
    {
        StopPttPolling();
        if (_registered)
        {
            UnregisterHotKey(_window.Handle, HOTKEY_ID);
            _registered = false;
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        HotkeyPressed?.Invoke(this, e);

        if (PushToTalkEnabled && !_pttKeyDown)
        {
            _pttKeyDown = true;
            StartPttPolling();
        }
    }

    private void StartPttPolling()
    {
        _pttPollTimer?.Dispose();
        _pttPollTimer = new System.Threading.Timer(PollKeyUp, null, 50, 50);
    }

    private void StopPttPolling()
    {
        _pttPollTimer?.Dispose();
        _pttPollTimer = null;
        _pttKeyDown = false;
    }

    private void PollKeyUp(object? state)
    {
        if (!_pttKeyDown) return;

        // Zkontroluj hlavní klávesu (VK) – pokud je uvolněná, uvolni PTT
        bool vkDown = (GetAsyncKeyState(_pttVirtualKey) & 0x8000) != 0;

        if (!vkDown)
        {
            _pttKeyDown = false;
            _pttPollTimer?.Dispose();
            _pttPollTimer = null;

            // Vyvolej HotkeyReleased na UI vlákně
            _window.BeginInvoke(() => HotkeyReleased?.Invoke(this, EventArgs.Empty));
        }
    }

    public void Dispose()
    {
        StopPttPolling();
        Unregister();
        _window.DestroyHandle();
    }

    // ---------------------------------------------------------------------------
    // Pomocný NativeWindow – zachytává WM_HOTKEY zprávy v message loop
    // ---------------------------------------------------------------------------
    private sealed class HotkeyWindow : NativeWindow
    {
        public event EventHandler? HotkeyPressed;

        // Slouží k přepnutí na UI vlákno z polling timeru
        private readonly Control _invoker;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
            _invoker = new Control();
            _invoker.CreateControl();
        }

        public void BeginInvoke(Action action)
        {
            if (_invoker.InvokeRequired)
                _invoker.BeginInvoke(action);
            else
                action();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
                HotkeyPressed?.Invoke(this, EventArgs.Empty);

            base.WndProc(ref m);
        }
    }
}
