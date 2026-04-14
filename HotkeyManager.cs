using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SttApp;

/// <summary>
/// Registruje globální klávesovou zkratku přes Win32 RegisterHotKey / UnregisterHotKey.
/// WM_HOTKEY zprávy jsou zachytávány přes NativeWindow připnuté k MessageLoop.
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

    public event EventHandler? HotkeyPressed;

    private readonly HotkeyWindow _window;
    private bool _registered;

    public HotkeyManager()
    {
        _window = new HotkeyWindow();
        _window.HotkeyPressed += OnHotkeyPressed;
    }

    public bool Register(int modifiers, int virtualKey)
    {
        if (_registered)
            Unregister();

        // Pridame MOD_NOREPEAT aby se nezacaly hromadit eventy pri drzeni klavesy
        _registered = RegisterHotKey(_window.Handle, HOTKEY_ID,
            (uint)modifiers | MOD_NOREPEAT, (uint)virtualKey);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_window.Handle, HOTKEY_ID);
            _registered = false;
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs e) => HotkeyPressed?.Invoke(this, e);

    public void Dispose()
    {
        Unregister();
        _window.DestroyHandle();
    }

    // ---------------------------------------------------------------------------
    // Pomocný NativeWindow – zachytává WM_HOTKEY zprávy v message loop
    // ---------------------------------------------------------------------------
    private sealed class HotkeyWindow : NativeWindow
    {
        public event EventHandler? HotkeyPressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
                HotkeyPressed?.Invoke(this, EventArgs.Empty);

            base.WndProc(ref m);
        }
    }
}
