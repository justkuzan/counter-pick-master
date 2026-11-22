using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CounterPickMaster;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new CounterWindow());
    }
}

internal sealed class CounterWindow : Form
{
    private const int PortraitSize = 50;
    private const int PortraitPadding = 2;
    private const int CornerRadius = 8;
    private static readonly Color Background = Color.FromArgb(0x1C, 0x1C, 0x1F);
    private static readonly Color Surface = Color.FromArgb(0x31, 0x31, 0x3C);
    private static readonly Color Recessed = Color.FromArgb(0x28, 0x28, 0x30);
    private static readonly Color Border = Color.FromArgb(0x42, 0x42, 0x54);
    private static readonly Color TextColor = Color.White;
    private static readonly Color Muted = Color.FromArgb(0xB7, 0xB7, 0xC9);
    private static readonly Color DisabledText = Color.FromArgb(0x7B, 0x7A, 0x8E);
    private static readonly Color StatusBlue = Color.FromArgb(0x81, 0xAC, 0xFF);
    private static readonly Color Selected = Color.FromArgb(0x28, 0x34, 0x4E);
    private static readonly Color LowSample = Color.FromArgb(0xFF, 0x6C, 0x81);

    private readonly DataService data = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CounterPickMaster", "matchups.json"));

    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBox search = new()
    {
        BorderStyle = BorderStyle.None, BackColor = Recessed, ForeColor = TextColor,
        PlaceholderText = "Имя чемпиона на русском или английском"
    };
    private readonly ListBox suggestions = new() { Dock = DockStyle.Fill, IntegralHeight = false, FormattingEnabled = true };
    private readonly ListView results = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
    private readonly Panel resultsHeader = new() { Dock = DockStyle.Top, Height = 25, BackColor = Recessed };
    private readonly ImageList portraits = new()
    {
        ColorDepth = ColorDepth.Depth32Bit,
        ImageSize = new Size(PortraitSize, PortraitSize + 2 * PortraitPadding)
    };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Panel statusIcon = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly System.Windows.Forms.Timer statusAnimation = new() { Interval = 50 };
    private readonly Font readyFont;
    private readonly Label patch = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true };
    private readonly Button retry = new() { Text = "Повторить", AutoSize = true, Visible = false };
    private readonly FlowLayoutPanel roleButtons = new() { Dock = DockStyle.Fill, WrapContents = false };
    private readonly Dictionary<string, RadioButton> buttonsByRole = [];
    private readonly ToolTip hint = new();
    private Snapshot? snapshot;
    private Champion? selected;
    private string role = "top";
    private bool updatingSuggestions;
    private bool synchronizing;
    private bool statusReady;
    private int spinnerAngle;
    private Dictionary<int, Champion> catalog = [];

    public CounterWindow()
    {
        Text = "Counter Pick Master";
        BackColor = Background;
        ForeColor = TextColor;
        using var executableIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (executableIcon is not null) Icon = (Icon)executableIcon.Clone();
        Font = new Font("Segoe UI", 10);
        readyFont = new Font(Font, FontStyle.Bold);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(730, 620);
        MinimumSize = new Size(670, 485);
        hint.BackColor = Surface;
        hint.ForeColor = TextColor;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 101));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Height = 32, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        header.Controls.Add(new Label
        {
            Text = "Золото · Solo/Duo · все серверы",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Muted, Margin = Padding.Empty
        }, 0, 0);
        patch.Text = "Патч OP.GG —";
        patch.ForeColor = Muted;
        patch.Margin = Padding.Empty;
        header.Controls.Add(patch, 1, 0);
        layout.Controls.Add(header, 0, 0);

        (string Code, string Title)[] roles =
        [
            ("top", "Топ"), ("jungle", "Лес"), ("mid", "Мид"), ("adc", "Низ"), ("support", "Поддержка")
        ];
        foreach (var (code, title) in roles)
        {
            var button = new RadioButton
            {
                Appearance = Appearance.Button, AutoSize = true, Text = title,
                Padding = new Padding(13, 2, 13, 2), Margin = new Padding(0, 3, 7, 5),
                Checked = code == role
            };
            StyleButton(button);
            button.CheckedChanged += (_, _) =>
            {
                if (button.Checked && role != code)
                {
                    role = code;
                    ShowResults();
                }
            };
            roleButtons.Controls.Add(button);
            buttonsByRole.Add(code, button);
        }

        suggestions.Format += (_, args) =>
        {
            if (args.ListItem is Champion champion) args.Value = $"{champion.Russian} ({champion.English})";
        };
        suggestions.BorderStyle = BorderStyle.None;
        suggestions.BackColor = Surface;
        suggestions.ForeColor = TextColor;
        suggestions.DrawMode = DrawMode.OwnerDrawFixed;
        suggestions.ItemHeight = 26;
        suggestions.DrawItem += DrawSuggestion;
        suggestions.HandleCreated += (_, _) => SetWindowTheme(suggestions.Handle, "DarkMode_Explorer", null);

        var searchCard = new RoundedPanel(Recessed, Border, 100) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 9) };
        searchCard.Controls.Add(search);
        searchCard.Resize += (_, _) =>
        {
            int left = ScalePixels(38);
            search.SetBounds(left, (searchCard.ClientSize.Height - search.PreferredHeight) / 2,
                Math.Max(0, searchCard.ClientSize.Width - left - ScalePixels(16)), search.PreferredHeight);
        };
        searchCard.Paint += (_, args) =>
        {
            var graphics = args.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int y = (searchCard.ClientSize.Height - ScalePixels(14)) / 2;
            using var pen = new Pen(Muted, 1.5f * DeviceDpi / 96);
            graphics.DrawEllipse(pen, ScalePixels(14), y, ScalePixels(10), ScalePixels(10));
            graphics.DrawLine(pen, ScalePixels(22), y + ScalePixels(8), ScalePixels(27), y + ScalePixels(13));
        };
        search.GotFocus += (_, _) => searchCard.SetOutline(StatusBlue);
        search.LostFocus += (_, _) => searchCard.SetOutline(Border);
        search.HandleCreated += (_, _) => SetWindowTheme(search.Handle, "DarkMode_Explorer", null);

        var suggestionsCard = new RoundedPanel(Surface, Border, 12)
        {
            Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 9), Padding = new Padding(9, 4, 8, 3)
        };
        suggestionsCard.Controls.Add(suggestions);

        results.SmallImageList = portraits;
        results.BackColor = Surface;
        results.ForeColor = TextColor;
        results.BorderStyle = BorderStyle.None;
        results.OwnerDraw = true;
        results.DrawItem += (_, args) => args.DrawDefault = false;
        results.DrawSubItem += DrawResultCell;
        results.HandleCreated += (_, _) => SetWindowTheme(results.Handle, "DarkMode_Explorer", null);
        results.Columns.Add("", 62);
        results.Columns.Add("Контрпик", 270);
        results.Columns.Add("Винрейт чемпиона", 167, HorizontalAlignment.Right);
        results.Columns.Add("Игры", 86, HorizontalAlignment.Right);
        results.HeaderStyle = ColumnHeaderStyle.None;
        results.MultiSelect = false;
        results.ShowItemToolTips = true;
        results.Resize += (_, _) => SizeResultColumns();
        var resultsCard = new RoundedPanel(Surface, Border, 12)
        {
            Dock = DockStyle.Fill, Padding = new Padding(1), Margin = Padding.Empty
        };
        resultsCard.Controls.Add(results);
        resultsCard.Controls.Add(resultsHeader);
        resultsHeader.Paint += DrawResultHeader;

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 14, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        status.Margin = Padding.Empty;
        retry.Margin = Padding.Empty;
        StyleButton(retry);
        footer.Controls.Add(statusIcon, 0, 0);
        footer.Controls.Add(status, 1, 0);
        footer.Controls.Add(retry, 2, 0);
        statusIcon.Paint += DrawStatusIcon;
        statusAnimation.Tick += (_, _) =>
        {
            spinnerAngle = (spinnerAngle + 24) % 360;
            statusIcon.Invalidate();
        };

        layout.Controls.Add(roleButtons, 0, 1);
        layout.Controls.Add(searchCard, 0, 2);
        layout.Controls.Add(suggestionsCard, 0, 3);
        layout.Controls.Add(resultsCard, 0, 4);
        layout.Controls.Add(footer, 0, 5);
        Controls.Add(layout);
        SetSearchEnabled(false);
        status.Text = "Обновляем данные OP.GG…";

        Shown += async (_, _) => await SynchronizeAsync();
        retry.Click += async (_, _) => await SynchronizeAsync();
        FormClosed += (_, _) => lifetime.Cancel();
        Disposed += (_, _) =>
        {
            statusAnimation.Dispose();
            readyFont.Dispose();
            portraits.Dispose();
        };
        search.TextChanged += (_, _) =>
        {
            selected = null;
            PopulateSuggestions();
            UpdateRoleAvailability();
            ShowResults();
        };
        search.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Down && suggestions.Items.Count > 0)
            {
                suggestions.Focus();
                suggestions.SelectedIndex = Math.Max(0, suggestions.SelectedIndex);
                args.SuppressKeyPress = true;
            }
            if (args.KeyCode == Keys.Enter && suggestions.Items.Count > 0)
            {
                suggestions.SelectedIndex = Math.Max(0, suggestions.SelectedIndex);
                SelectChampion();
                args.SuppressKeyPress = true;
            }
        };
        suggestions.MouseClick += (_, args) =>
        {
            if (suggestions.IndexFromPoint(args.Location) >= 0) SelectChampion();
        };
        suggestions.KeyDown += (_, args) =>
        {
            if (args.KeyCode != Keys.Enter) return;
            SelectChampion();
            args.SuppressKeyPress = true;
        };
    }

    private void SetSearchEnabled(bool enabled)
    {
        search.Enabled = suggestions.Enabled = roleButtons.Enabled = enabled;
    }

    private void UpdateRoleAvailability()
    {
        string? firstAvailable = null;
        foreach (var (code, button) in buttonsByRole)
        {
            bool available = selected is null || snapshot?.Find(code, selected.Id).Length > 0;
            button.Enabled = available;
            if (available) firstAvailable ??= code;
        }

        if (firstAvailable is null)
        {
            buttonsByRole[role].Checked = false;
            return;
        }
        if (!buttonsByRole[role].Enabled) role = firstAvailable;
        buttonsByRole[role].Checked = true;
    }

    private void SetStatus(string text, bool busy = false, bool ready = false)
    {
        status.Text = text;
        statusReady = ready;
        status.Font = ready ? readyFont : Font;
        status.ForeColor = ready ? StatusBlue : Muted;
        statusAnimation.Enabled = busy;
        statusIcon.Invalidate();
    }

    private void DrawStatusIcon(object? sender, PaintEventArgs e)
    {
        if (!statusReady && !statusAnimation.Enabled) return;

        float size = 16f * statusIcon.DeviceDpi / 96f;
        e.Graphics.TranslateTransform((statusIcon.Width - size) / 2, (statusIcon.Height - size) / 2);
        e.Graphics.ScaleTransform(size / 16, size / 16);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(StatusBlue, 2) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (statusReady) e.Graphics.DrawLines(pen, [new Point(2, 8), new Point(6, 12), new Point(14, 4)]);
        else e.Graphics.DrawArc(pen, 2, 2, 12, 12, spinnerAngle, 260);
    }

    private int ScalePixels(int pixels) => (pixels * DeviceDpi + 48) / 96;

    private void StyleButton(ButtonBase button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.UseVisualStyleBackColor = false;
        button.BackColor = Background;
        button.ForeColor = TextColor;
        button.Paint += (_, args) =>
        {
            bool active = button is RadioButton { Checked: true };
            bool hovered = button.Enabled && button.ClientRectangle.Contains(button.PointToClient(Cursor.Position));
            Color fill = !button.Enabled ? Recessed : active ? Selected : hovered ? Border : Surface;
            Color foreground = !button.Enabled ? DisabledText : active ? StatusBlue : TextColor;
            args.Graphics.Clear(Background);
            args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = RoundedPanel.OutlinePath(button.ClientRectangle, ScalePixels(9));
            using var brush = new SolidBrush(fill);
            using var pen = new Pen(button.Focused ? StatusBlue : Border);
            args.Graphics.FillPath(brush, shape);
            if (button.Enabled) args.Graphics.DrawPath(pen, shape);
            TextRenderer.DrawText(args.Graphics, button.Text, button.Font, button.ClientRectangle, foreground,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        button.EnabledChanged += (_, _) => button.Invalidate();
        button.MouseEnter += (_, _) => button.Invalidate();
        button.MouseLeave += (_, _) => button.Invalidate();
    }

    private void DrawSuggestion(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool isSelected = (e.State & DrawItemState.Selected) != 0;
        using var brush = new SolidBrush(isSelected ? Selected : Surface);
        e.Graphics.FillRectangle(brush, e.Bounds);
        var textBounds = Rectangle.Inflate(e.Bounds, -ScalePixels(7), 0);
        TextRenderer.DrawText(e.Graphics, suggestions.GetItemText(suggestions.Items[e.Index]), Font,
            textBounds, isSelected ? StatusBlue : TextColor, TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private void DrawResultHeader(object? sender, PaintEventArgs e)
    {
        using var divider = new Pen(Border);
        int left = 0;
        foreach (ColumnHeader column in results.Columns)
        {
            var bounds = new Rectangle(left, 0, column.Width, resultsHeader.Height);
            DrawCellText(e.Graphics, column.Text, bounds, Muted, column.Index);
            left += column.Width;
        }
        e.Graphics.DrawLine(divider, 0, resultsHeader.Height - 1, resultsHeader.Width, resultsHeader.Height - 1);
    }

    private void SizeResultColumns()
    {
        int fixedWidth = results.Columns[0].Width + results.Columns[2].Width + results.Columns[3].Width;
        int width = Math.Max(ScalePixels(160), results.ClientSize.Width - fixedWidth - SystemInformation.VerticalScrollBarWidth);
        if (results.Columns[1].Width != width) results.Columns[1].Width = width;
        resultsHeader.Invalidate();
    }

    private void DrawResultCell(object? sender, DrawListViewSubItemEventArgs e)
    {
        var bounds = e.Bounds;
        bool selectedRow = e.Item?.Selected == true;
        Color rowColor = selectedRow ? Selected : e.ItemIndex % 2 == 0 ? Surface : Recessed;
        using var brush = new SolidBrush(rowColor);
        using var divider = new Pen(Border);
        int rowEdge = e.ColumnIndex == results.Columns.Count - 1 ? results.ClientSize.Width : bounds.Right;
        e.Graphics.FillRectangle(brush, Rectangle.FromLTRB(bounds.Left, bounds.Top, rowEdge, bounds.Bottom));
        e.Graphics.DrawLine(divider, bounds.Left, bounds.Bottom - 1, rowEdge, bounds.Bottom - 1);
        if (e.ColumnIndex == 0 && e.Item is { } row)
        {
            int index = portraits.Images.IndexOfKey(row.ImageKey);
            if (index >= 0) portraits.Draw(e.Graphics, bounds.Left + ScalePixels(8),
                bounds.Top + (bounds.Height - portraits.ImageSize.Height) / 2, index);
        }
        else
        {
            DrawCellText(e.Graphics, e.SubItem?.Text ?? "", bounds, e.Item?.ForeColor ?? TextColor, e.ColumnIndex);
        }
    }

    private void DrawCellText(Graphics graphics, string text, Rectangle bounds, Color color, int column)
    {
        bounds = Rectangle.Inflate(bounds, -ScalePixels(8), 0);
        TextFormatFlags alignment = column >= 2 ? TextFormatFlags.Right : TextFormatFlags.Left;
        TextRenderer.DrawText(graphics, text, Font, bounds, color, alignment | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        int caption = ColorTranslator.ToWin32(Background);
        DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr handle, string theme, string? subId);

    private async Task SynchronizeAsync()
    {
        if (synchronizing) return;
        synchronizing = true;
        retry.Visible = false;
        hint.SetToolTip(status, null);
        SetStatus("Обновляем данные OP.GG…", busy: true);
        int shownCount = 0;
        var progress = new Progress<string>(message =>
        {
            if (!synchronizing || IsDisposed) return;
            int start = message.LastIndexOf(' ') + 1;
            int end = message.IndexOf('/', start);
            if (end >= start && int.TryParse(message.AsSpan(start, end - start), out int count))
            {
                if (count < shownCount) return;
                shownCount = count;
            }
            status.Text = message;
        });
        try
        {
            if (snapshot is null)
            {
                var cached = await data.LoadCacheAsync(lifetime.Token);
                if (IsDisposed || lifetime.IsCancellationRequested) return;
                if (cached is not null)
                {
                    ApplySnapshot(cached);
                    search.Focus();
                }
            }

            var previous = snapshot;
            var refreshed = await Task.Run(() => data.RefreshAsync(previous, progress, lifetime.Token), lifetime.Token);
            if (IsDisposed || lifetime.IsCancellationRequested) return;
            ApplySnapshot(refreshed);
            SetStatus("Данные готовы", ready: true);
            hint.SetToolTip(status, $"Последняя проверка: {refreshed.CheckedAt.ToLocalTime():dd.MM.yyyy HH:mm}");
        }
        catch (Exception error) when (!lifetime.IsCancellationRequested)
        {
            if (IsDisposed) return;
            SetStatus(snapshot is null ? "Не удалось обновить данные" : "Не удалось обновить · показаны сохранённые данные");
            hint.SetToolTip(status, error.Message);
            retry.Visible = true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            synchronizing = false;
        }
    }

    private void ApplySnapshot(Snapshot current)
    {
        bool sameTables = ReferenceEquals(snapshot?.Matchups, current.Matchups);
        snapshot = current;
        string sourceTime = current.SourceTime.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        patch.Text = current.PatchNotesDate is { } date
            ? $"Патч OP.GG {current.Patch} от {date:dd.MM.yyyy}"
            : $"Патч OP.GG {current.Patch}";
        hint.SetToolTip(patch, $"Все серверы, Solo/Duo. Таблицы OP.GG загружены {sourceTime}.");
        if (sameTables) return;

        catalog = current.Champions.ToDictionary(champion => champion.Id);
        if (selected is not null) selected = catalog.GetValueOrDefault(selected.Id);
        SetSearchEnabled(true);
        PopulateSuggestions();
        UpdateRoleAvailability();
        ShowResults();
    }

    private void PopulateSuggestions()
    {
        if (snapshot is null) return;
        updatingSuggestions = true;
        suggestions.BeginUpdate();
        suggestions.Items.Clear();
        suggestions.Items.AddRange(DataService.Search(snapshot.Champions, search.Text).Cast<object>().ToArray());
        if (selected is not null) suggestions.SelectedItem = selected;
        suggestions.EndUpdate();
        updatingSuggestions = false;
    }

    private void SelectChampion()
    {
        if (updatingSuggestions || suggestions.SelectedItem is not Champion champion) return;
        selected = champion;
        UpdateRoleAvailability();
        ShowResults();
    }

    private void ShowResults()
    {
        results.BeginUpdate();
        results.Items.Clear();
        if (snapshot is not null && selected is not null)
        {
            var rows = snapshot.Ranked(role, selected.Id);
            results.Columns[2].Text = $"Винрейт: {selected.Russian}";
            foreach (var counter in rows)
            {
                if (!catalog.TryGetValue(counter.Id, out var champion)) continue;
                var item = new ListViewItem([
                    "",
                    $"{champion.Russian} ({champion.English})",
                    counter.Rate.ToString("P2", CultureInfo.CurrentCulture),
                    counter.Games.ToString("N0", CultureInfo.CurrentCulture)
                ]);
                item.ImageKey = GetPortraitKey(champion.Id);
                if (counter.Games < 100)
                {
                    item.ForeColor = LowSample;
                    item.ToolTipText = "Малая выборка: меньше 100 игр.";
                }
                results.Items.Add(item);
            }
            if (rows.Length == 0)
                results.Items.Add(new ListViewItem(["", snapshot.Find(role, selected.Id).Length == 0
                    ? "Нет данных по этой роли"
                    : "Нет матчапов с винрейтом ниже 50%"]));
        }
        else
        {
            results.Columns[2].Text = "Винрейт чемпиона";
        }
        results.EndUpdate();
        SizeResultColumns();
    }

    private string GetPortraitKey(int championId)
    {
        string key = championId.ToString(CultureInfo.InvariantCulture);
        if (portraits.Images.ContainsKey(key)) return key;

        using var stream = typeof(CounterWindow).Assembly.GetManifestResourceStream($"Portraits.{key}.png");
        if (stream is null) return "";

        using var source = Image.FromStream(stream);
        using var resized = new Bitmap(source, new Size(PortraitSize, PortraitSize));
        using var masked = new Bitmap(portraits.ImageSize.Width, portraits.ImageSize.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(masked);
        using var brush = new TextureBrush(resized);
        using var outline = new GraphicsPath();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        brush.TranslateTransform(0, PortraitPadding);
        int diameter = 2 * CornerRadius;
        int farEdge = PortraitSize - diameter;
        outline.AddArc(0, PortraitPadding, diameter, diameter, 180, 90);
        outline.AddArc(farEdge, PortraitPadding, diameter, diameter, 270, 90);
        outline.AddArc(farEdge, farEdge + PortraitPadding, diameter, diameter, 0, 90);
        outline.AddArc(0, farEdge + PortraitPadding, diameter, diameter, 90, 90);
        outline.CloseFigure();
        graphics.FillPath(brush, outline);

        _ = portraits.Handle;
        portraits.Images.Add(key, masked);
        return key;
    }
}

internal sealed class RoundedPanel(Color fill, Color outline, int radius) : Panel
{
    private Color outlineColor = outline;

    public void SetOutline(Color color)
    {
        outlineColor = color;
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width < 2 || Height < 2) return;
        using var shape = OutlinePath(new Rectangle(0, 0, Width + 1, Height + 1), radius * DeviceDpi / 96);
        var previous = Region;
        Region = new Region(shape);
        previous?.Dispose();
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(fill);

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width > 1 && Height > 1)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = OutlinePath(ClientRectangle, radius * DeviceDpi / 96);
            using var pen = new Pen(outlineColor);
            e.Graphics.DrawPath(pen, shape);
        }
        base.OnPaint(e);
    }

    public static GraphicsPath OutlinePath(Rectangle bounds, int radius)
    {
        int width = bounds.Width - 1;
        int height = bounds.Height - 1;
        int diameter = Math.Min(2 * radius, Math.Min(width, height));
        var path = new GraphicsPath();
        if (diameter < 2) return path;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Left + width - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Left + width - diameter, bounds.Top + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Top + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
