using System.Runtime.InteropServices;
using System.Text;

namespace SttApp;

/// <summary>
/// Vlozi text do libovolne Windows aplikace.
///
/// Strategie:
///   WM_Paste (SendMessage)   - Win32 Edit/RichEdit/Scintilla/Word
///   WM_Paste (PostMessage)   - Chrome/Electron/WebView2 na Chrome_RenderWidgetHostHWND
///   Ctrl+V (SendInput)       - genericka zaloha s obnovenim foreground
///   TypeChars (SendInput)    - konzolova okna + UIPI zaloha
/// </summary>
public static class TextInjector
{
    #region Win32 constants

    private const uint INPUT_KEYBOARD    = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP   = 0x0002;
    private const uint KEYEVENTF_DOWN    = 0;
    private const ushort VK_CONTROL      = 0x11;
    private const ushort VK_V            = 0x56;
    private const uint WM_PASTE          = 0x0302;
    private const uint CF_UNICODE        = 13;
    private const uint GMEM_MOVE         = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION u; }

    // Na 64-bit Windows: sizeof(MOUSEINPUT)=32, sizeof(INPUT)=40 (4+4padding+32)
    // Bez Size=32 by Marshal.SizeOf<INPUT>()=28 → SendInput vraci err=87
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan;
        public uint dwFlags; public uint time;
        public IntPtr dwExtraInfo;
    }

    #endregion

    #region P/Invoke

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] p, int cb);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool   SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool   BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern uint   GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool   AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern void   SwitchToThisWindow(IntPtr h, bool fAlt);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool   PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr h);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr h, StringBuilder buf, int max);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr lParam);
    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint fmt, IntPtr h);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint f, UIntPtr sz);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool   GlobalUnlock(IntPtr h);

    #endregion

    // -----------------------------------------------------------------------
    // Window class helpers
    // -----------------------------------------------------------------------

    private static string WinClass(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool IsWin32Edit(string cls) =>
        cls.Equals("Edit",              StringComparison.OrdinalIgnoreCase) ||
        cls.StartsWith("RichEdit",      StringComparison.OrdinalIgnoreCase) ||
        cls.StartsWith("RICHEDIT",      StringComparison.OrdinalIgnoreCase) ||
        cls.Equals("Scintilla",         StringComparison.OrdinalIgnoreCase) ||
        cls.Equals("RichEditD2DPT",     StringComparison.OrdinalIgnoreCase) ||
        cls.Equals("RichEdit20WPT",     StringComparison.OrdinalIgnoreCase) ||
        cls.Equals("RichEdit20A",       StringComparison.OrdinalIgnoreCase) ||
        cls.Equals("RichEdit20W",       StringComparison.OrdinalIgnoreCase);

    // Word editor (Outlook compose, Word dokument) – potrebuje SetFocus + Ctrl+V, ne WM_PASTE
    private static bool IsWordEditor(string cls) =>
        cls.Equals("_WwG", StringComparison.OrdinalIgnoreCase);

    private static bool IsChromium(string cls) =>
        cls.Equals("Chrome_WidgetWin_1",      StringComparison.OrdinalIgnoreCase) ||
        cls.Equals("Chrome_WidgetWin_0",      StringComparison.OrdinalIgnoreCase) ||
        cls.StartsWith("Chrome_",             StringComparison.OrdinalIgnoreCase);

    private static bool IsConsole(string cls) =>
        cls.Equals("ConsoleWindowClass",            StringComparison.OrdinalIgnoreCase) ||
        cls.Equals("CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase) ||
        cls.StartsWith("WT_Window",                 StringComparison.OrdinalIgnoreCase);

    // Najde prvni Chrome_RenderWidgetHostHWND uvnitr Chrome okna
    // Tento child hwnd prijima WM_PASTE a preposlani na DOM element
    private static IntPtr FindChromeRenderHost(IntPtr parent)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, (h, _) =>
        {
            string cls = WinClass(h);
            if (cls.Equals("Chrome_RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false; // stop
            }
            return true; // continue
        }, IntPtr.Zero);
        return found;
    }

    // -----------------------------------------------------------------------
    // Clipboard
    // -----------------------------------------------------------------------

    private static bool SetClipboard(string text)
    {
        int retry = 6;
        while (retry-- > 0)
        {
            if (OpenClipboard(IntPtr.Zero)) break;
            System.Threading.Thread.Sleep(30);
        }
        if (retry < 0) return false;
        try
        {
            EmptyClipboard();
            var bytes = (UIntPtr)((text.Length + 1) * 2);
            var hMem = GlobalAlloc(GMEM_MOVE, bytes);
            if (hMem == IntPtr.Zero) return false;
            var ptr = GlobalLock(hMem);
            if (ptr == IntPtr.Zero) return false;
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                Marshal.WriteInt16(ptr, text.Length * 2, 0);
            }
            finally { GlobalUnlock(hMem); }
            SetClipboardData(CF_UNICODE, hMem);
            return true;
        }
        finally { CloseClipboard(); }
    }

    // -----------------------------------------------------------------------
    // Foreground focus obnova (obchazi foreground lock)
    // -----------------------------------------------------------------------

    private static void RestoreForeground(IntPtr target, uint myTid)
    {
        IntPtr curFg    = GetForegroundWindow();
        uint   curFgTid = GetWindowThreadProcessId(curFg, out _);
        bool attached   = curFgTid != 0 && curFgTid != myTid;
        if (attached) AttachThreadInput(myTid, curFgTid, true);
        SwitchToThisWindow(target, false);
        SetForegroundWindow(target);
        BringWindowToTop(target);
        if (attached) AttachThreadInput(myTid, curFgTid, false);
        // Cekej max 800ms
        for (int i = 0; i < 26; i++)
        {
            System.Threading.Thread.Sleep(30);
            if (GetForegroundWindow() == target) break;
        }
    }

    private static IntPtr GetFocusedChild(IntPtr wnd, uint myTid)
    {
        uint wndTid = GetWindowThreadProcessId(wnd, out _);
        if (wndTid == 0 || wndTid == myTid) return GetFocus();
        AttachThreadInput(myTid, wndTid, true);
        IntPtr ctrl = GetFocus();
        AttachThreadInput(myTid, wndTid, false);
        return ctrl;
    }

    // -----------------------------------------------------------------------
    // Verejne API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Zachyti focused Win32Edit child ve spravny okamzik (pri stisku hotkeje).
    /// Vraci IntPtr.Zero pokud fokusovany child neni Win32Edit.
    /// </summary>
    public static IntPtr CaptureFocusedEditChild(IntPtr topWindow)
    {
        if (topWindow == IntPtr.Zero) return IntPtr.Zero;
        uint myTid = GetCurrentThreadId();
        IntPtr child = GetFocusedChild(topWindow, myTid);
        if (child == IntPtr.Zero) return IntPtr.Zero;
        string cls = WinClass(child);
        WhisperTranscriber.AppLog($"CaptureFocusedEditChild: hwnd={topWindow:X} child={child:X} cls={cls}");
        return (IsWin32Edit(cls) || IsWordEditor(cls)) ? child : IntPtr.Zero;
    }

    public static Task PasteViaClipboardAsync(string text, IntPtr targetHwnd = default, IntPtr hintEditChild = default)
    {
        if (string.IsNullOrEmpty(text)) return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                // 1. Nastav clipboard
                if (!SetClipboard(text))
                {
                    WhisperTranscriber.AppLog("Paste: SetClipboard FAILED");
                    return;
                }
                System.Threading.Thread.Sleep(40);

                if (targetHwnd == IntPtr.Zero)
                    targetHwnd = GetForegroundWindow();

                uint myTid    = GetCurrentThreadId();
                string topCls = WinClass(targetHwnd);

                WhisperTranscriber.AppLog($"Paste: hwnd={targetHwnd:X} cls={topCls} hint={hintEditChild:X}");

                // ── HINT: primy edit child zachyceny v momentu stisku hotkeje ───────
                if (hintEditChild != IntPtr.Zero)
                {
                    string hintCls = WinClass(hintEditChild);

                    if (IsWordEditor(hintCls))
                    {
                        // Word/Outlook _WwG: ignoruje WM_PASTE, potrebuje SetFocus + Ctrl+V
                        WhisperTranscriber.AppLog($"Paste: Word editor hint {hintCls} ctrl={hintEditChild:X} – SetFocus+Ctrl+V");
                        RestoreForeground(targetHwnd, myTid);
                        // Explicitni focus na Word editor window
                        uint wndTid = GetWindowThreadProcessId(hintEditChild, out _);
                        if (wndTid != 0 && wndTid != myTid)
                        {
                            AttachThreadInput(myTid, wndTid, true);
                            SetFocus(hintEditChild);
                            AttachThreadInput(myTid, wndTid, false);
                        }
                        System.Threading.Thread.Sleep(200);
                        uint sentW = SendInput(4, new[]
                        {
                            Vk(VK_CONTROL, false), Vk(VK_V, false),
                            Vk(VK_V, true),        Vk(VK_CONTROL, true),
                        }, Marshal.SizeOf<INPUT>());
                        if (sentW == 0)
                        {
                            int errW = Marshal.GetLastWin32Error();
                            WhisperTranscriber.AppLog($"Paste: Word Ctrl+V blocked (err={errW}), TypeChars");
                            TypeText(text);
                        }
                        System.Threading.Thread.Sleep(60);
                        return;
                    }

                    if (IsWin32Edit(hintCls))
                    {
                        WhisperTranscriber.AppLog($"Paste: WM_PASTE (hint) → {hintCls} ctrl={hintEditChild:X}");
                        SendMessage(hintEditChild, WM_PASTE, IntPtr.Zero, IntPtr.Zero);
                        System.Threading.Thread.Sleep(30);
                        return;
                    }
                }

                // ── A. Win32 Edit/RichEdit ────────────────────────────────
                // Nejprve obnov focus okna, pak znovu zjisti focusovany child.
                // Dulezite: GetFocusedChild pred RestoreForeground muze vracet
                // stary (read-only) child napr. Outlook reading pane.
                if (GetForegroundWindow() != targetHwnd)
                    RestoreForeground(targetHwnd, myTid);
                else
                    System.Threading.Thread.Sleep(50);

                IntPtr child = GetFocusedChild(targetHwnd, myTid);
                string childCls = child != IntPtr.Zero ? WinClass(child) : topCls;
                IntPtr editTarget = child != IntPtr.Zero ? child : targetHwnd;

                if (IsWin32Edit(childCls))
                {
                    WhisperTranscriber.AppLog($"Paste: WM_PASTE → Win32Edit {childCls} ctrl={editTarget:X}");
                    SendMessage(editTarget, WM_PASTE, IntPtr.Zero, IntPtr.Zero);
                    System.Threading.Thread.Sleep(30);
                    return;
                }

                // ── B. Chrome/Electron/WebView2 ───────────────────────────
                // WM_PASTE na RenderWidgetHostHWND nefunguje spolehlivc pokud
                // okno nema focus (VS Code Copilot Chat, Figma, apod.).
                // Spravny postup: prehod focus → SetFocus na render widget →
                // pockat na obnoveni DOM focusu → Ctrl+V pres SendInput.
                if (IsChromium(topCls) || IsChromium(childCls))
                {
                    // Krok 1: Prehod Chrome/Electron okno do popredi
                    if (GetForegroundWindow() != targetHwnd)
                        RestoreForeground(targetHwnd, myTid);
                    else
                        System.Threading.Thread.Sleep(80);

                    IntPtr renderHost = FindChromeRenderHost(targetHwnd);
                    if (renderHost != IntPtr.Zero)
                    {
                        // Krok 2: Nastav Win32 focus na render widget
                        // Chrome/Electron pri WM_SETFOCUS obnovi DOM focus na posledni aktivni input
                        uint renderTid = GetWindowThreadProcessId(renderHost, out _);
                        if (renderTid != 0 && renderTid != myTid)
                        {
                            AttachThreadInput(myTid, renderTid, true);
                            SetFocus(renderHost);
                            AttachThreadInput(myTid, renderTid, false);
                        }
                        // Krok 3: Cekej az Chrome zavola JS focus event a UI obnoví fokus inputu
                        System.Threading.Thread.Sleep(280);
                        WhisperTranscriber.AppLog($"Paste: SendInput Ctrl+V → Chrome renderHost={renderHost:X}");
                    }
                    else
                    {
                        System.Threading.Thread.Sleep(150);
                        WhisperTranscriber.AppLog("Paste: Chrome RenderHost not found, Ctrl+V to top window");
                    }

                    // Krok 4: Ctrl+V – Chrome okno je nyni popredi, ma spravny focus
                    uint sentC = SendInput(4, new[]
                    {
                        Vk(VK_CONTROL, false), Vk(VK_V, false),
                        Vk(VK_V, true),        Vk(VK_CONTROL, true),
                    }, Marshal.SizeOf<INPUT>());
                    if (sentC == 0)
                    {
                        int errC = Marshal.GetLastWin32Error();
                        WhisperTranscriber.AppLog($"Paste: Chrome Ctrl+V blocked (err={errC}), TypeChars");
                        TypeText(text);
                    }
                    System.Threading.Thread.Sleep(80);
                    return;
                }

                // ── C. Konzola ────────────────────────────────────────────
                if (IsConsole(topCls) || IsConsole(childCls))
                {
                    WhisperTranscriber.AppLog("Paste: TypeChars (console)");
                    if (GetForegroundWindow() != targetHwnd)
                        RestoreForeground(targetHwnd, myTid);
                    System.Threading.Thread.Sleep(80);
                    TypeText(text);
                    return;
                }

                // ── D. Genericke okno (WPF, Qt, ...) → Ctrl+V ────────────
                if (GetForegroundWindow() != targetHwnd)
                {
                    RestoreForeground(targetHwnd, myTid);
                    System.Threading.Thread.Sleep(200);
                }
                else
                {
                    System.Threading.Thread.Sleep(80);
                }

                // Re-check po obnoveni focusu - mozna je ted focusovany Edit
                IntPtr child2    = GetFocusedChild(targetHwnd, myTid);
                string childCls2 = child2 != IntPtr.Zero ? WinClass(child2) : topCls;
                if (IsWin32Edit(childCls2))
                {
                    IntPtr editTarget2 = child2 != IntPtr.Zero ? child2 : targetHwnd;
                    WhisperTranscriber.AppLog($"Paste: WM_PASTE (phase2) → {childCls2}");
                    SendMessage(editTarget2, WM_PASTE, IntPtr.Zero, IntPtr.Zero);
                    System.Threading.Thread.Sleep(30);
                    return;
                }

                // Ctrl+V
                WhisperTranscriber.AppLog("Paste: SendInput Ctrl+V");
                uint sent = SendInput(4, new[]
                {
                    Vk(VK_CONTROL, false), Vk(VK_V, false),
                    Vk(VK_V, true),        Vk(VK_CONTROL, true),
                }, Marshal.SizeOf<INPUT>());

                if (sent == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    WhisperTranscriber.AppLog($"Paste: SendInput blocked (err={err}), TypeChars fallback");
                    TypeText(text);
                }
                System.Threading.Thread.Sleep(60);
            }
            catch (Exception ex)
            {
                WhisperTranscriber.AppLog($"Paste exception: {ex.Message}");
            }
            finally { tcs.TrySetResult(); }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return Task.WhenAny(tcs.Task, Task.Delay(5000)).ContinueWith(_ => { });
    }

    // -----------------------------------------------------------------------
    // Unicode typetext - znak po znaku (konzola, UIPI zaloha)
    // -----------------------------------------------------------------------

    public static void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var inputs = new INPUT[text.Length * 2];
        int idx = 0;
        foreach (char c in text)
        {
            inputs[idx++] = UnicodeKey(c, false);
            inputs[idx++] = UnicodeKey(c, true);
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Vk(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u    = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : KEYEVENTF_DOWN } }
    };

    private static INPUT UnicodeKey(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u    = new INPUTUNION { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } }
    };
}