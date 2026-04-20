using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SttApp;

public sealed class HistoryForm : Form
{
    private static readonly Color ColBg     = Color.FromArgb(245, 247, 250);
    private static readonly Color ColHeader = Color.FromArgb(22, 100, 200);
    private static readonly Color ColAccent = Color.FromArgb(22, 100, 200);
    private static readonly Color ColText   = Color.FromArgb(30, 40, 60);
    private static readonly Color ColSub    = Color.FromArgb(90, 105, 130);
    private static readonly Color ColSep    = Color.FromArgb(210, 218, 230);
    private static readonly Color ColHover  = Color.FromArgb(230, 238, 250);

    private readonly TranscriptionHistory _history;
    private Panel _scrollPanel = null!;

    public HistoryForm(TranscriptionHistory history)
    {
        _history = history;

        Text            = "Historie přepisů";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox     = true;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = ColBg;
        Font            = new Font("Segoe UI", 9.5f);
        AutoScaleMode   = AutoScaleMode.Font;
        MinimumSize     = new Size(500, 400);

        int maxH = Screen.PrimaryScreen!.WorkingArea.Height - 80;
        ClientSize = new Size(680, Math.Min(700, maxH));

        BuildLayout();
    }

    private void BuildLayout()
    {
        // === HEADER ===
        var header = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = ColHeader };
        header.Controls.Add(new Label
        {
            Text = "Historie přepisů",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 14),
        });

        // === FOOTER (buttons) ===
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = ColBg };

        var btnClear = new Button
        {
            Text = "Smazat vše",
            Size = new Size(110, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(200, 60, 60),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Location = new Point(20, 8),
        };
        btnClear.FlatAppearance.BorderSize = 0;
        btnClear.Click += (_, _) =>
        {
            var result = MessageBox.Show("Opravdu smazat celou historii?",
                "Historie přepisů", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result == DialogResult.Yes)
            {
                _history.Clear();
                RebuildList();
            }
        };
        footer.Controls.Add(btnClear);

        var btnClose = new Button
        {
            Text = "Zavřít",
            Size = new Size(100, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = ColAccent,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        btnClose.Location = new Point(footer.Width - btnClose.Width - 20, 8);
        btnClose.FlatAppearance.BorderSize = 0;
        btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 75, 170);
        btnClose.Click += (_, _) => Close();
        footer.Controls.Add(btnClose);

        // === SCROLLABLE LIST ===
        _scrollPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = ColBg,
        };

        // Fill musí být přidán PŘED Top/Bottom
        Controls.Add(_scrollPanel);
        Controls.Add(header);
        Controls.Add(footer);

        _scrollPanel.Resize += (_, _) => RebuildList();

        RebuildList();
    }

    private void RebuildList()
    {
        _scrollPanel.SuspendLayout();
        _scrollPanel.Controls.Clear();
        _scrollPanel.AutoScrollMinSize = Size.Empty;

        int panelWidth = _scrollPanel.ClientSize.Width;
        if (panelWidth < 100) panelWidth = ClientSize.Width - 40;

        if (_history.Items.Count == 0)
        {
            _scrollPanel.Controls.Add(new Label
            {
                Text = "Žádné přepisy v historii.",
                AutoSize = true,
                ForeColor = ColSub,
                Font = new Font("Segoe UI", 10f, FontStyle.Italic),
                Location = new Point(20, 20),
            });
            _scrollPanel.ResumeLayout();
            return;
        }

        int cardWidth = panelWidth - 40;
        int y = 12;

        foreach (var item in _history.Items)
        {
            var card = CreateCard(item, cardWidth);
            card.Location = new Point(14, y);
            _scrollPanel.Controls.Add(card);
            y += card.Height + 8;
        }

        _scrollPanel.AutoScrollMinSize = new Size(0, y + 12);
        _scrollPanel.ResumeLayout();
    }

    private Panel CreateCard(TranscriptionHistoryItem item, int width)
    {
        var card = new Panel
        {
            Width = width,
            BackColor = Color.White,
            Padding = new Padding(12, 8, 12, 8),
            Cursor = Cursors.Hand,
        };

        // Zaoblený okraj
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(ColSep, 1);
            var rect = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
            using var path = RoundedRect(rect, 6);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(pen, path);
        };

        // Timestamp + duration
        var timeLabel = new Label
        {
            Text = $"{item.Timestamp:dd.MM.yyyy HH:mm}  ·  {item.DurationSeconds:F1}s  ·  {item.Language.ToUpperInvariant()}",
            AutoSize = true,
            ForeColor = ColSub,
            Font = new Font("Segoe UI", 8f),
            Location = new Point(12, 8),
            BackColor = Color.Transparent,
        };
        card.Controls.Add(timeLabel);

        // Text preview (max 3 lines)
        string preview = item.Text.Length > 300 ? item.Text[..300] + "…" : item.Text;

        var textFont = new Font("Segoe UI", 9.5f);
        var textSize = TextRenderer.MeasureText(preview, textFont,
            new Size(width - 30, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);

        var textLabel = new Label
        {
            Text = preview,
            AutoSize = false,
            Size = new Size(width - 30, Math.Min(textSize.Height, 80)),
            ForeColor = ColText,
            Font = textFont,
            Location = new Point(12, 28),
            BackColor = Color.Transparent,
        };
        card.Controls.Add(textLabel);

        // Buttons row
        int textBottom = 28 + textLabel.Height;

        var iconFont = new Font("Segoe MDL2 Assets", 10f);
        var textFontBtn = new Font("Segoe UI", 8f, FontStyle.Bold);
        string copyIcon = "\uE8C8";
        string copyText = " Kopírovat";
        bool copied = false;

        var btnCopy = new Button
        {
            FlatStyle = FlatStyle.Flat,
            BackColor = ColAccent,
            ForeColor = Color.White,
            Size = new Size(116, 28),
            Cursor = Cursors.Hand,
            TabStop = false,
            Location = new Point(12, textBottom + 6),
        };
        btnCopy.FlatAppearance.BorderSize = 0;
        btnCopy.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 75, 170);
        btnCopy.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            string icon = copied ? "\uE73E" : copyIcon;
            string label = copied ? " Zkopírováno" : copyText;
            var iconSize = TextRenderer.MeasureText(icon, iconFont);
            var labelSize = TextRenderer.MeasureText(label, textFontBtn);
            int totalW = iconSize.Width + labelSize.Width - 8;
            int x = (btnCopy.Width - totalW) / 2;
            int yIcon = (btnCopy.Height - iconSize.Height) / 2;
            int yText = (btnCopy.Height - labelSize.Height) / 2;
            TextRenderer.DrawText(g, icon, iconFont, new Point(x, yIcon), Color.White);
            TextRenderer.DrawText(g, label, textFontBtn, new Point(x + iconSize.Width - 4, yText), Color.White);
        };
        btnCopy.Click += (_, _) =>
        {
            Clipboard.SetText(item.Text);
            copied = true;
            btnCopy.Invalidate();
            var timer = new System.Windows.Forms.Timer { Interval = 1500 };
            timer.Tick += (_, _) => { copied = false; btnCopy.Invalidate(); timer.Dispose(); };
            timer.Start();
        };
        card.Controls.Add(btnCopy);

        var btnDelete = new Button
        {
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(240, 240, 240),
            ForeColor = Color.FromArgb(150, 60, 60),
            Size = new Size(36, 28),
            Cursor = Cursors.Hand,
            TabStop = false,
            Location = new Point(btnCopy.Right + 6, textBottom + 6),
        };
        btnDelete.FlatAppearance.BorderSize = 0;
        btnDelete.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 220, 220);
        btnDelete.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var sz = TextRenderer.MeasureText("\uE74D", iconFont);
            int x = (btnDelete.Width - sz.Width) / 2;
            int y = (btnDelete.Height - sz.Height) / 2;
            TextRenderer.DrawText(g, "\uE74D", iconFont, new Point(x, y), Color.FromArgb(150, 60, 60));
        };
        btnDelete.Click += (_, _) =>
        {
            _history.Remove(item);
            RebuildList();
        };
        card.Controls.Add(btnDelete);

        card.Height = textBottom + 38;

        // Hover efekt
        void SetHover(bool hover)
        {
            card.BackColor = hover ? ColHover : Color.White;
            timeLabel.BackColor = card.BackColor;
            textLabel.BackColor = card.BackColor;
        }
        card.MouseEnter += (_, _) => SetHover(true);
        card.MouseLeave += (_, _) => { if (!card.ClientRectangle.Contains(card.PointToClient(Cursor.Position))) SetHover(false); };
        timeLabel.MouseEnter += (_, _) => SetHover(true);
        textLabel.MouseEnter += (_, _) => SetHover(true);

        return card;
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
