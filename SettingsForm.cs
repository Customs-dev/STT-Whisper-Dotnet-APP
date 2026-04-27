using System.Drawing;
using System.Windows.Forms;

namespace SttApp;

public sealed class SettingsForm : Form
{
    private static readonly Color ColBg     = Color.FromArgb(245, 247, 250);
    private static readonly Color ColHeader = Color.FromArgb(22, 100, 200);
    private static readonly Color ColText   = Color.FromArgb(30, 40, 60);
    private static readonly Color ColSub    = Color.FromArgb(90, 105, 130);

    private readonly AppSettings _settings;
    private readonly TextBox       _tbHotkey;
    private readonly TextBox       _tbModelPath;
    private readonly TextBox       _tbLanguage;
    private readonly CheckBox      _chkWs;
    private readonly NumericUpDown _numWsPort;
    private readonly NumericUpDown _numChunkSeconds;
    private readonly CheckBox      _chkClipboard;
    private readonly ComboBox      _cmbRecordingMode;
    private bool                   _capturingHotkey;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;

        Text            = "Nastavení – Prompto";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = ColBg;
        Font            = new Font("Segoe UI", 9.5f);
        AutoScaleMode   = AutoScaleMode.Font;
        ClientSize      = new Size(500, 990);

        // TABLE – důležité: Fill se musí přidat PŘED Top
        var tbl = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = false,
            BackColor = ColBg,
            Padding = new Padding(20, 14, 20, 14),
        };
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(tbl);

        // HEADER
        var header = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = ColHeader };
        header.Controls.Add(new Label {
            Text = "Nastavení", ForeColor = Color.White,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            AutoSize = true, Location = new Point(18, 8)
        });
        header.Controls.Add(new Label {
            Text = "Prompto – hlasový přepis",
            ForeColor = Color.FromArgb(180, 210, 255),
            Font = new Font("Segoe UI", 9f), AutoSize = true, Location = new Point(20, 46)
        });
        Controls.Add(header);

        int row = 0;

        void AddSep(string title) {
            var lbl = new Label {
                Text = title.ToUpperInvariant(), AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = ColHeader,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                Height = 26, TextAlign = System.Drawing.ContentAlignment.BottomLeft,
                Margin = new Padding(0, row == 0 ? 0 : 10, 0, 2),
            };
            tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tbl.Controls.Add(lbl, 0, row);
            tbl.SetColumnSpan(lbl, 2);
            row++;
            var sep = new Panel { Height = 1, Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(210,218,230), Margin = new Padding(0,0,0,8) };
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 9));
            tbl.Controls.Add(sep, 0, row);
            tbl.SetColumnSpan(sep, 2);
            row++;
        }

        void AddRow(string label, Control ctrl, string? note = null) {
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            var lbl = new Label {
                Text = label, AutoSize = false, Dock = DockStyle.Fill,
                ForeColor = ColText, TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            };
            tbl.Controls.Add(lbl, 0, row);
            ctrl.Dock = DockStyle.Fill;
            ctrl.Margin = new Padding(0, 3, 0, 3);
            tbl.Controls.Add(ctrl, 1, row);
            row++;
            if (note != null) {
                var noteLbl = new Label {
                    Text = note, AutoSize = true,
                    MaximumSize = new Size(320, 0),
                    ForeColor = ColSub, Font = new Font("Segoe UI", 8f),
                    Margin = new Padding(0, 0, 0, 6),
                };
                tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                tbl.Controls.Add(noteLbl, 1, row);
                row++;
            }
        }

        // KLÁVESOVÁ ZKRATKA
        AddSep("Klávesová zkratka");
        _tbHotkey = new TextBox {
            Text = FormatHotkey(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey),
            ReadOnly = true, BackColor = Color.White,
        };
        _tbHotkey.MouseClick += (_, _) => {
            if (_capturingHotkey) return;
            _capturingHotkey = true;
            _tbHotkey.Text = "Stiskněte zkratku…";
            _tbHotkey.BackColor = Color.FromArgb(255, 250, 220);
        };
        _tbHotkey.KeyDown += OnHotkeyKeyDown;
        _tbHotkey.KeyUp   += OnHotkeyKeyUp;
        _tbHotkey.LostFocus += (_, _) => {
            if (_capturingHotkey) {
                _capturingHotkey = false;
                _tbHotkey.Text = FormatHotkey(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey);
                _tbHotkey.BackColor = Color.White;
            }
        };
        AddRow("Klávesová zkratka:", _tbHotkey, "Klikněte a stiskněte kombinaci kláves");

        _cmbRecordingMode = new ComboBox {
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.White,
        };
        _cmbRecordingMode.Items.AddRange(new object[] { "Toggle (1× start, 2× stop)", "Push-to-Talk (drž = nahrávej)" });
        _cmbRecordingMode.SelectedIndex = _settings.RecordingMode == RecordingMode.PushToTalk ? 1 : 0;
        AddRow("Režim nahrávání:", _cmbRecordingMode, "Toggle = stisk zapne/vypne, PTT = drž klávesu pro nahrávání");

        // WHISPER MODEL
        AddSep("Whisper model");
        _tbModelPath = new TextBox { Text = _settings.ModelPath, BackColor = Color.White };
        _tbModelPath.TextChanged += (_, _) => _settings.ModelPath = _tbModelPath.Text.Trim();
        AddRow("Cesta k modelu:", _tbModelPath, "Cesta ke .bin souboru s modelem Whisper");

        _tbLanguage = new TextBox { Text = _settings.Language, BackColor = Color.White };
        _tbLanguage.TextChanged += (_, _) => _settings.Language = _tbLanguage.Text.Trim();
        AddRow("Jazyk:", _tbLanguage, "Např. cs, en, de, sk");

        // SCHRÁNKA
        AddSep("Schránka");
        _chkClipboard = new CheckBox { Text = "Kopírovat přepis do schránky", Checked = _settings.CopyToClipboard };
        AddRow("", _chkClipboard, "Přepsaný text se automaticky uloží do schránky (Ctrl+V)");

        // WEBSOCKET SERVER
        AddSep("WebSocket server");
        _numChunkSeconds = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 60,
            Value = Math.Clamp(_settings.ChunkIntervalSeconds, 0, 60),
            BackColor = Color.White,
            Enabled = _settings.EnableWebSocket,
        };
        _numWsPort = new NumericUpDown
        {
            Minimum = 1024,
            Maximum = 65535,
            Value = Math.Clamp(_settings.WebSocketPort, 1024, 65535),
            ThousandsSeparator = false,
            Enabled = _settings.EnableWebSocket,
            BackColor = Color.White,
        };
        _chkWs = new CheckBox { Text = "Zapnout WebSocket server", Checked = _settings.EnableWebSocket };
        _chkWs.CheckedChanged += (_, _) =>
        {
            _numWsPort.Enabled = _chkWs.Checked;
            _numChunkSeconds.Enabled = _chkWs.Checked;
        };
        AddRow("", _chkWs);

        AddRow("Port:", _numWsPort, "Rozsah 1024–65535. Výchozí: 5050");
        AddRow("Chunk interval (s):", _numChunkSeconds, "0 = vypnuto, doporučeno 2–4 s pro živý stream");

        // AUTH TOKEN – read-only zobrazení s možností regenerace a kopírování
        var tbToken = new TextBox
        {
            Text = _settings.WebSocketAuthToken,
            ReadOnly = true,
            BackColor = Color.White,
            Font = new Font("Consolas", 8.5f),
        };
        AddRow("Auth token:", tbToken, "Klienti se musí připojit s ?token=... v URL nebo Authorization: Bearer ...");

        var tokenBtnPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        var btnCopyToken = new Button { Text = "Kopírovat", AutoSize = true, FlatStyle = FlatStyle.Flat };
        btnCopyToken.Click += (_, _) =>
        {
            try { Clipboard.SetText(tbToken.Text); }
            catch { /* clipboard může být zamčen */ }
        };
        var btnRegenToken = new Button { Text = "Vygenerovat nový", AutoSize = true, FlatStyle = FlatStyle.Flat };
        btnRegenToken.Click += (_, _) =>
        {
            if (MessageBox.Show(this,
                    "Po regeneraci tokenu se všichni připojení klienti odpojí a budou potřebovat nové URL.\nPokračovat?",
                    "Regenerace tokenu", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            _settings.WebSocketAuthToken = string.Empty;
            _settings.Save(); // Save() vygeneruje nový token
            tbToken.Text = _settings.WebSocketAuthToken;
        };
        tokenBtnPanel.Controls.Add(btnCopyToken);
        tokenBtnPanel.Controls.Add(btnRegenToken);
        AddRow("", tokenBtnPanel);

        // SPACER
        tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var spacer = new Panel { BackColor = ColBg, Dock = DockStyle.Fill };
        tbl.Controls.Add(spacer, 0, row);
        tbl.SetColumnSpan(spacer, 2);
        row++;

        // BUTTONS
        tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var btnPanel = new FlowLayoutPanel {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill, BackColor = ColBg,
        };

        Button MakeBtn(string text, bool primary) {
            var b = new Button {
                Text = text, Size = new Size(100, 32), FlatStyle = FlatStyle.Flat,
                BackColor = primary ? ColHeader : Color.FromArgb(210,218,230),
                ForeColor = primary ? Color.White : ColText,
                Font = new Font("Segoe UI", 9.5f, primary ? FontStyle.Bold : FontStyle.Regular),
                Margin = new Padding(6, 4, 0, 4), Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        var btnOk     = MakeBtn("OK", true);
        var btnCancel = MakeBtn("Zrušit", false);
        btnOk.Click     += (_, _) => { ApplyToSettings(); DialogResult = DialogResult.OK; Close(); };
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        btnPanel.Controls.AddRange(new Control[] { btnOk, btnCancel });
        tbl.Controls.Add(btnPanel, 0, row);
        tbl.SetColumnSpan(btnPanel, 2);
        tbl.RowCount = row + 1;

        FormClosing += (_, _) => ApplyToSettings();
    }

    private void OnHotkeyKeyDown(object? s, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.SuppressKeyPress = true;
        if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
        _settings.HotkeyVirtualKey = (int)e.KeyCode;
        _settings.HotkeyModifiers  = 0;
        if (e.Control) _settings.HotkeyModifiers |= 0x0002;
        if (e.Shift)   _settings.HotkeyModifiers |= 0x0004;
        if (e.Alt)     _settings.HotkeyModifiers |= 0x0001;
        _tbHotkey.Text = FormatHotkey(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey);
        _tbHotkey.BackColor = Color.White;
        _capturingHotkey = false;
    }

    private void OnHotkeyKeyUp(object? s, KeyEventArgs e)
    {
        if (_capturingHotkey) e.SuppressKeyPress = true;
    }

    private static string FormatHotkey(int mod, int vk)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((mod & 0x0002) != 0) parts.Add("Ctrl");
        if ((mod & 0x0004) != 0) parts.Add("Shift");
        if ((mod & 0x0001) != 0) parts.Add("Alt");
        var key = (Keys)vk;
        parts.Add(key == Keys.None ? "?" : key.ToString());
        return string.Join(" + ", parts);
    }

    private void ApplyToSettings()
    {
        _settings.WebSocketPort = (int)_numWsPort.Value;
        _settings.ChunkIntervalSeconds = (int)_numChunkSeconds.Value;
        _settings.EnableWebSocket = _chkWs.Checked;
        _settings.CopyToClipboard = _chkClipboard.Checked;
        _settings.Language        = _tbLanguage.Text.Trim();
        _settings.ModelPath       = _tbModelPath.Text.Trim();
        _settings.RecordingMode   = _cmbRecordingMode.SelectedIndex == 1
            ? RecordingMode.PushToTalk : RecordingMode.Toggle;
    }
}