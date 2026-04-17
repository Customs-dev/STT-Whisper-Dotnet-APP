using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using QRCoder;

namespace SttApp;

public sealed class AboutForm : Form
{
    private static readonly Color ColBg     = Color.FromArgb(245, 247, 250);
    private static readonly Color ColHeader = Color.FromArgb(22, 100, 200);
    private static readonly Color ColAccent = Color.FromArgb(22, 100, 200);
    private static readonly Color ColText   = Color.FromArgb(30, 40, 60);
    private static readonly Color ColSub    = Color.FromArgb(90, 105, 130);
    private static readonly Color ColSep    = Color.FromArgb(210, 218, 230);

    public AboutForm()
    {
        Text            = "O aplikaci Prompto";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = ColBg;
        Font            = new Font("Segoe UI", 9.5f);
        AutoScaleMode   = AutoScaleMode.Font;
        ClientSize      = new Size(680, 900);

        BuildLayout();

        // Po sestavení layoutu nastav výšku na max. dostupnou výšku obrazovky
        // (ScrollableControl zajišťuje scrollování pokud je obsah vyšší)
        int maxH = Screen.FromControl(this).WorkingArea.Height - 60;
        ClientSize = new Size(ClientSize.Width, Math.Min(1200, maxH));
    }

    private void BuildLayout()
    {
        // === HEADER ===
        var header = new Panel { Dock = DockStyle.Top, Height = 90, BackColor = ColHeader };
        header.Controls.Add(new Label {
            Text = "Prompto", ForeColor = Color.White,
            Font = new Font("Segoe UI", 22f, FontStyle.Bold),
            AutoSize = true, Location = new Point(20, 10)
        });
        header.Controls.Add(new Label {
            Text = $"Hlasový přepis s AI  ·  verze {GetAppVersion()}",
            ForeColor = Color.FromArgb(180, 210, 255),
            Font = new Font("Segoe UI", 9f), AutoSize = true, Location = new Point(22, 62)
        });

        // === SCROLLABLE BODY ===
        // DŮLEŽITÉ: Fill ovládací prvek musí být přidán PŘED Top, jinak ho Top překryje
        var scroll = new ScrollableControl {
            Dock = DockStyle.Fill, AutoScroll = true, BackColor = ColBg
        };
        Controls.Add(scroll);
        Controls.Add(header);

        // Vnitřní panel s pevnou šířkou
        var body = new FlowLayoutPanel {
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            BackColor     = ColBg,
            Padding       = new Padding(24, 16, 24, 20),
            Width         = 680,
        };
        scroll.Controls.Add(body);

        // === CO PROMPTO DĚLÁ ===
        body.Controls.Add(MakeSection("Co Prompto dělá?"));

        body.Controls.Add(MakeRichText(
            "Prompto je tichá aplikace v oznamovací oblasti (systray), která rozpoznává váš hlas pomocí AI modelu Whisper od OpenAI. Vše běží lokálně na vašem počítači – žádná data nejdou na internet.\n\n" +
            "Jak to funguje:\n" +
            "  1.  Stiskněte klávesovou zkratku (výchozí: Ctrl + Shift + F8)\n" +
            "  2.  Mluvte – Prompto nahrává přes mikrofon\n" +
            "  3.  Stiskněte zkratku znovu pro ukončení nahrávání\n" +
            "  4.  AI přepíše řeč na text a vloží ho na aktuální pozici kurzoru\n\n" +
            "Přepsaný text se automaticky ukládá do schránky (clipboard) – můžete ho kdykoli znovu vložit pomocí Ctrl+V.\n\n" +
            "Klávesová zkratka funguje v libovolné aplikaci – textový editor, e-mail, prohlížeč, chatovací nástroj, VS Code a další.\n\n" +
            "Whisper model běží na GPU (Vulkan) pro rychlý přepis. Při nedostupnosti GPU se automaticky přepne na CPU."
        ));

        body.Controls.Add(MakeSep());

        // === AUTOR ===
        body.Controls.Add(MakeSection("Autor"));

        var authorTbl = new TableLayoutPanel {
            ColumnCount = 2, AutoSize = true,
            BackColor = ColBg,
            Margin = new Padding(0, 0, 0, 8),
        };
        authorTbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        authorTbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void AuthorRow(string label, Control ctrl, int row) {
            authorTbl.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = ColSub, Font = new Font("Segoe UI", 9.5f), Margin = new Padding(0,4,8,4) }, 0, row);
            authorTbl.Controls.Add(ctrl, 1, row);
        }
        AuthorRow("Jméno:", new Label { Text = "Roman Kovařík", AutoSize = true, ForeColor = ColText, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(0,4,0,4) }, 0);

        var lnkMail = new LinkLabel { Text = "roman@kovarik.eu", AutoSize = true, LinkColor = ColAccent, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(0,4,0,4) };
        lnkMail.LinkClicked += (_, _) => OpenUrl("mailto:roman@kovarik.eu");
        AuthorRow("E-mail:", lnkMail, 1);

        var lnkWeb = new LinkLabel { Text = "www.kovarik.eu", AutoSize = true, LinkColor = ColAccent, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(0,4,0,4) };
        lnkWeb.LinkClicked += (_, _) => OpenUrl("https://www.kovarik.eu");
        AuthorRow("Web:", lnkWeb, 2);

        body.Controls.Add(authorTbl);
        body.Controls.Add(MakeSep());

        // === PODPORA ===
        body.Controls.Add(MakeSection("Podpořte vývoj"));
        body.Controls.Add(MakeRichText(
            "QR platbou ve výši 50 Kč podpoříte autora aplikace a další vývoj.\nDěkuji!  🙏"
        ));

        // QR + info vedle sebe
        var qrRow = new FlowLayoutPanel {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true, WrapContents = false,
            BackColor = ColBg,
            Margin = new Padding(0, 8, 0, 8),
        };

        // QR rámeček
        var qrBox = new PictureBox {
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(180, 180),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 20, 0),
            Image = GenerateQr(),
        };
        qrRow.Controls.Add(qrBox);

        // Platební info
        var info = new FlowLayoutPanel {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoSize = true,
            BackColor = ColBg,
        };
        Label Inf(string t, Font f, Color c) { var l = new Label { Text=t, AutoSize=true, ForeColor=c, Font=f, Margin=new Padding(0,2,0,2) }; return l; }
        info.Controls.Add(Inf("Číslo účtu:",        new Font("Segoe UI", 8.5f),                  ColSub));
        info.Controls.Add(Inf("150186349 / 0300",   new Font("Segoe UI", 11f, FontStyle.Bold),   ColText));
        info.Controls.Add(Inf("",                   new Font("Segoe UI", 4f),                    ColBg));
        info.Controls.Add(Inf("Banka:",              new Font("Segoe UI", 8.5f),                  ColSub));
        info.Controls.Add(Inf("ČSOB",               new Font("Segoe UI", 11f, FontStyle.Bold),   ColText));
        info.Controls.Add(Inf("",                   new Font("Segoe UI", 4f),                    ColBg));
        info.Controls.Add(Inf("Částka:",             new Font("Segoe UI", 8.5f),                  ColSub));
        info.Controls.Add(Inf("50,00 CZK",          new Font("Segoe UI", 16f, FontStyle.Bold),   ColAccent));
        info.Controls.Add(Inf("",                   new Font("Segoe UI", 4f),                    ColBg));
        info.Controls.Add(Inf("← naskenujte QR kód",new Font("Segoe UI", 8.5f, FontStyle.Italic),ColSub));
        info.Controls.Add(Inf("   bankovní aplikací",new Font("Segoe UI", 8.5f, FontStyle.Italic),ColSub));
        qrRow.Controls.Add(info);
        body.Controls.Add(qrRow);

        body.Controls.Add(MakeSep());

        // Zavřít
        var btnClose = new Button {
            Text = "Zavřít", Size = new Size(100, 34),
            FlatStyle = FlatStyle.Flat, BackColor = ColAccent, ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Cursor = Cursors.Hand,
            Margin = new Padding(0, 6, 0, 4),
        };
        btnClose.FlatAppearance.BorderSize = 0;
        btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 75, 170);
        btnClose.Click += (_, _) => Close();

        var btnWrap = new FlowLayoutPanel {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true, WrapContents = false, BackColor = ColBg,
            Width = 632,
        };
        btnWrap.Controls.Add(btnClose);
        body.Controls.Add(btnWrap);
    }

    // -----------------------------------------------------------------------
    private static Label MakeSection(string text) => new()
    {
        Text = text.ToUpperInvariant(), AutoSize = true,
        ForeColor = Color.FromArgb(22, 100, 200),
        Font = new Font("Segoe UI", 8f, FontStyle.Bold),
        Margin = new Padding(0, 4, 0, 6),
    };

    private static Label MakeRichText(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(632, 0),   // wrap at 632px, height unlimited
        ForeColor = Color.FromArgb(30, 40, 60),
        Font = new Font("Segoe UI", 9.5f),
        Margin = new Padding(0, 0, 0, 8),
    };

    private static Panel MakeSep() => new()
    {
        Height = 1, Width = 632,
        BackColor = Color.FromArgb(210, 218, 230),
        Margin = new Padding(0, 8, 0, 10),
    };

    private static Bitmap GenerateQr()
    {
        const string spd = "SPD*1.0*ACC:CZ6603000000000150186349*AM:50.00*CC:CZK*MSG:Podpora vyvoje Prompto";
        using var gen  = new QRCodeGenerator();
        using var data = gen.CreateQrCode(spd, QRCodeGenerator.ECCLevel.M);
        using var qr   = new QRCode(data);
        return qr.GetGraphic(8, Color.FromArgb(20, 20, 40), Color.White, drawQuietZones: true);
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private static string GetAppVersion()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly();
        var info = asm?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is System.Reflection.AssemblyInformationalVersionAttribute[] { Length: > 0 } attrs
            ? attrs[0].InformationalVersion : null;
        if (info is not null)
        {
            // Remove build metadata (e.g. "+sha.abc123") if present
            int plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        var ver = asm?.GetName().Version;
        return ver is null ? "?" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
    }
}