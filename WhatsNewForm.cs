using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SttApp;

/// <summary>
/// Formulář „Co je nového" – zobrazí se automaticky po aktualizaci na novou verzi.
/// </summary>
public sealed class WhatsNewForm : Form
{
    private static readonly Color ColBg     = Color.FromArgb(245, 247, 250);
    private static readonly Color ColHeader = Color.FromArgb(22, 100, 200);
    private static readonly Color ColText   = Color.FromArgb(30, 40, 60);
    private static readonly Color ColSub    = Color.FromArgb(90, 105, 130);
    private static readonly Color ColSep    = Color.FromArgb(210, 218, 230);
    private static readonly Color ColCard   = Color.White;
    private static readonly Color ColBadge  = Color.FromArgb(22, 100, 200);

    private readonly string _currentVersion;

    /// <summary>
    /// Vytvoří formulář zobrazující novinky od verze <paramref name="sinceVersion"/>.
    /// Pokud je null, zobrazí jen aktuální verzi.
    /// </summary>
    public WhatsNewForm(string currentVersion, string? sinceVersion = null)
    {
        _currentVersion = currentVersion;

        Text            = "Prompto – Co je nového";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = ColBg;
        Font            = new Font("Segoe UI", 9.5f);
        AutoScaleMode   = AutoScaleMode.Font;

        int maxH = Screen.PrimaryScreen!.WorkingArea.Height - 80;
        ClientSize = new Size(520, Math.Min(600, maxH));

        BuildLayout(sinceVersion);
    }

    private void BuildLayout(string? sinceVersion)
    {
        // === HEADER ===
        var header = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = ColHeader };
        header.Paint += (_, e) =>
        {
            using var big = new Font("Segoe UI", 18f, FontStyle.Bold);
            using var small = new Font("Segoe UI", 9f);
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            TextRenderer.DrawText(e.Graphics, "Co je nového", big,
                new Point(20, 8), Color.White);
            TextRenderer.DrawText(e.Graphics, $"verze {_currentVersion}", small,
                new Point(22, 44), Color.FromArgb(180, 210, 255));
        };

        // === FOOTER with OK button ===
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 55, BackColor = ColBg };
        var btnOk = new Button
        {
            Text = "Rozumím",
            Size = new Size(120, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = ColHeader,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        btnOk.FlatAppearance.BorderSize = 0;
        btnOk.Click += (_, _) => Close();
        footer.Controls.Add(btnOk);
        footer.Layout += (_, _) =>
        {
            btnOk.Location = new Point(
                (footer.ClientSize.Width - btnOk.Width) / 2,
                (footer.ClientSize.Height - btnOk.Height) / 2);
        };

        // === SCROLLABLE BODY ===
        var scrollPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = ColBg,
            Padding = new Padding(0),
        };

        // Filter entries to show
        var entries = GetEntriesToShow(sinceVersion);

        int y = 16;
        foreach (var entry in entries)
        {
            var card = BuildVersionCard(entry, scrollPanel.ClientSize.Width - 48);
            card.Location = new Point(20, y);
            scrollPanel.Controls.Add(card);
            y += card.Height + 12;
        }

        scrollPanel.AutoScrollMinSize = new Size(0, y + 8);

        // Add controls (Fill must be added before Dock.Top/Bottom)
        Controls.Add(scrollPanel);
        Controls.Add(footer);
        Controls.Add(header);

        AcceptButton = btnOk;
    }

    private Panel BuildVersionCard(ChangelogEntry entry, int width)
    {
        int padding = 16;
        int innerWidth = width - padding * 2;

        // Measure content height
        int contentHeight = 0;

        // Version badge + title line
        using var titleFont = new Font("Segoe UI", 11f, FontStyle.Bold);
        var titleSize = TextRenderer.MeasureText(
            $"v{entry.Version} – {entry.Title}", titleFont,
            new Size(innerWidth, 0), TextFormatFlags.WordBreak);
        contentHeight += titleSize.Height + 8;

        // Change items
        using var itemFont = new Font("Segoe UI", 9.5f);
        foreach (var change in entry.Changes)
        {
            var itemSize = TextRenderer.MeasureText(
                $"  •  {change}", itemFont,
                new Size(innerWidth - 16, 0), TextFormatFlags.WordBreak);
            contentHeight += itemSize.Height + 4;
        }

        contentHeight += padding * 2;

        var card = new Panel
        {
            Size = new Size(width, contentHeight),
            BackColor = ColCard,
        };
        card.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Left accent bar
            using var accentBrush = new SolidBrush(ColBadge);
            g.FillRectangle(accentBrush, 0, 0, 4, card.Height);

            // Separator line at bottom
            using var sepPen = new Pen(ColSep);
            g.DrawLine(sepPen, 0, card.Height - 1, card.Width, card.Height - 1);

            int yPos = padding;

            // Version + title
            using var tFont = new Font("Segoe UI", 11f, FontStyle.Bold);
            var title = $"v{entry.Version} – {entry.Title}";
            var tSize = TextRenderer.MeasureText(g, title, tFont,
                new Size(innerWidth, 0), TextFormatFlags.WordBreak);
            TextRenderer.DrawText(g, title, tFont,
                new Rectangle(padding, yPos, innerWidth, tSize.Height),
                ColText, TextFormatFlags.WordBreak);
            yPos += tSize.Height + 8;

            // Items
            using var iFont = new Font("Segoe UI", 9.5f);
            foreach (var change in entry.Changes)
            {
                var text = $"  •  {change}";
                var iSize = TextRenderer.MeasureText(g, text, iFont,
                    new Size(innerWidth - 16, 0), TextFormatFlags.WordBreak);
                TextRenderer.DrawText(g, text, iFont,
                    new Rectangle(padding + 8, yPos, innerWidth - 16, iSize.Height),
                    ColSub, TextFormatFlags.WordBreak);
                yPos += iSize.Height + 4;
            }
        };

        return card;
    }

    private static List<ChangelogEntry> GetEntriesToShow(string? sinceVersion)
    {
        if (sinceVersion == null)
        {
            // Show just the latest entry
            return Changelog.Entries.Take(1).ToList();
        }

        // Show all entries newer than sinceVersion
        var result = new List<ChangelogEntry>();
        foreach (var entry in Changelog.Entries)
        {
            if (string.Equals(entry.Version, sinceVersion, StringComparison.Ordinal))
                break;
            result.Add(entry);
        }

        // If nothing found (sinceVersion not in changelog), show latest
        return result.Count > 0 ? result : Changelog.Entries.Take(1).ToList();
    }

    // --- Persistence: track which version changelog was last seen ---

    private static readonly string SeenVersionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Prompto", "last_seen_version.txt");

    /// <summary>
    /// Vrátí true, pokud by se měl formulář zobrazit (aktuální verze je novější než naposledy viděná).
    /// </summary>
    public static bool ShouldShow(string currentVersion)
    {
        try
        {
            if (!File.Exists(SeenVersionPath))
                return true;

            var seen = File.ReadAllText(SeenVersionPath).Trim();
            return !string.Equals(seen, currentVersion, StringComparison.Ordinal);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Vrátí verzi, která byla naposledy viděná (nebo null).
    /// </summary>
    public static string? GetLastSeenVersion()
    {
        try
        {
            if (File.Exists(SeenVersionPath))
                return File.ReadAllText(SeenVersionPath).Trim();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Uloží aktuální verzi jako „viděnou".
    /// </summary>
    public static void MarkAsSeen(string currentVersion)
    {
        try
        {
            var dir = Path.GetDirectoryName(SeenVersionPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SeenVersionPath, currentVersion);
        }
        catch { }
    }
}
