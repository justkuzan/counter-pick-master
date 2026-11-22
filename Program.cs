using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
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

    private readonly DataService data = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CounterPickMaster", "matchups.json"));

    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBox search = new() { Dock = DockStyle.Top, PlaceholderText = "Имя чемпиона на русском или английском" };
    private readonly ListBox suggestions = new() { Dock = DockStyle.Fill, IntegralHeight = false, FormattingEnabled = true };
    private readonly ListView results = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
    private readonly ImageList portraits = new()
    {
        ColorDepth = ColorDepth.Depth32Bit,
        ImageSize = new Size(PortraitSize, PortraitSize + 2 * PortraitPadding)
    };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label patch = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true };
    private readonly Button retry = new() { Text = "Повторить", AutoSize = true, Visible = false };
    private readonly Button offline = new() { Text = "Открыть сохранённые", AutoSize = true, Visible = false };
    private readonly FlowLayoutPanel roleButtons = new() { Dock = DockStyle.Fill, WrapContents = false };
    private readonly ToolTip hint = new();
    private Snapshot? snapshot;
    private Champion? selected;
    private string role = "top";
    private bool updatingSuggestions;
    private bool synchronizing;
    private Dictionary<int, Champion> catalog = [];

    public CounterWindow()
    {
        Text = "Counter Pick Master";
        using var executableIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (executableIcon is not null) Icon = (Icon)executableIcon.Clone();
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(730, 620);
        MinimumSize = new Size(670, 485);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Height = 32, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        header.Controls.Add(new Label
        {
            Text = "Золото · Solo/Duo · все серверы",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        patch.Text = "Патч OP.GG —";
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
                Padding = new Padding(13, 1, 13, 1), Checked = code == role
            };
            button.CheckedChanged += (_, _) =>
            {
                if (button.Checked)
                {
                    role = code;
                    ShowResults();
                }
            };
            roleButtons.Controls.Add(button);
        }

        suggestions.Format += (_, args) =>
        {
            if (args.ListItem is Champion champion) args.Value = $"{champion.Russian} ({champion.English})";
        };
        results.SmallImageList = portraits;
        results.Columns.Add("", 62);
        results.Columns.Add("Контрпик", 270);
        results.Columns.Add("Винрейт чемпиона", 167, HorizontalAlignment.Right);
        results.Columns.Add("Игры", 86, HorizontalAlignment.Right);
        results.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        results.MultiSelect = false;
        results.ShowItemToolTips = true;

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        status.MinimumSize = new Size(0, 30);
        footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(offline, 1, 0);
        footer.Controls.Add(retry, 2, 0);

        layout.Controls.Add(roleButtons, 0, 1);
        layout.Controls.Add(search, 0, 2);
        layout.Controls.Add(suggestions, 0, 3);
        layout.Controls.Add(results, 0, 4);
        layout.Controls.Add(footer, 0, 5);
        Controls.Add(layout);
        SetSearchEnabled(false);
        status.Text = "Обновляем данные OP.GG…";

        Shown += async (_, _) => await SynchronizeAsync();
        retry.Click += async (_, _) => await SynchronizeAsync();
        offline.Click += (_, _) => EnableSnapshot(saved: true);
        FormClosed += (_, _) => lifetime.Cancel();
        Disposed += (_, _) => portraits.Dispose();
        search.TextChanged += (_, _) =>
        {
            selected = null;
            PopulateSuggestions();
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

    private async Task SynchronizeAsync()
    {
        if (synchronizing) return;
        synchronizing = true;
        retry.Visible = offline.Visible = false;
        SetSearchEnabled(false);
        status.Text = "Обновляем данные OP.GG…";
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
            snapshot ??= await data.LoadCacheAsync(lifetime.Token);
            snapshot = await data.RefreshAsync(snapshot, progress, lifetime.Token);
            if (!IsDisposed) EnableSnapshot(saved: false);
        }
        catch (Exception error) when (!lifetime.IsCancellationRequested)
        {
            if (IsDisposed) return;
            status.Text = "Не удалось обновить данные";
            hint.SetToolTip(status, error.Message);
            retry.Visible = true;
            offline.Visible = snapshot is not null;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            synchronizing = false;
        }
    }

    private void EnableSnapshot(bool saved)
    {
        if (snapshot is null) return;
        catalog = snapshot.Champions.ToDictionary(champion => champion.Id);
        string sourceTime = snapshot.SourceTime.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        patch.Text = snapshot.PatchNotesDate is { } date
            ? $"Патч OP.GG {snapshot.Patch} от {date:dd.MM.yyyy}"
            : $"Патч OP.GG {snapshot.Patch}";
        hint.SetToolTip(patch, $"Все серверы, Solo/Duo. Таблицы OP.GG загружены {sourceTime}.");
        status.Text = saved ? "Открыта сохранённая статистика" : "Данные готовы";
        hint.SetToolTip(status, $"Последняя проверка: {snapshot.CheckedAt.ToLocalTime():dd.MM.yyyy HH:mm}");
        offline.Visible = false;
        SetSearchEnabled(true);
        PopulateSuggestions();
        ShowResults();
        search.Focus();
    }

    private void PopulateSuggestions()
    {
        if (snapshot is null) return;
        updatingSuggestions = true;
        suggestions.BeginUpdate();
        suggestions.Items.Clear();
        suggestions.Items.AddRange(DataService.Search(snapshot.Champions, search.Text).Cast<object>().ToArray());
        suggestions.EndUpdate();
        updatingSuggestions = false;
    }

    private void SelectChampion()
    {
        if (updatingSuggestions || suggestions.SelectedItem is not Champion champion) return;
        selected = champion;
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
                    item.ForeColor = Color.Firebrick;
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
