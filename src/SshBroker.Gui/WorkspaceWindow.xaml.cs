using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SshBroker.Core;

namespace SshBroker.Gui;

public partial class WorkspaceWindow : Window
{
    private readonly Func<RpcRequest, Task<RpcResponse>> _call;
    private readonly Dictionary<string, (TabItem Tab, SessionView View, TextBlock Label)> _tabs = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _refreshing, _paletteSync;
    private string _palette = "Light";
    private readonly string? _settingsDirectory;
    private bool _lastMaximized;
    public bool CloseToTrayEnabled { get; set; }
    public bool IsInBackground { get; private set; }
    public int ConnectedTargetCount => _tabs.Values.Count(item => item.View.View.State == "connected");
    public event Action? HiddenToTray;
    public void ShowFromTray()
    {
        IsInBackground = false;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = _lastMaximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
    }
    public WorkspaceWindow(string pipe, Func<RpcRequest, Task<RpcResponse>> call, string? settingsDirectory = null)
    {
        _call = call; _settingsDirectory = settingsDirectory; InitializeComponent();
        RestorePlacement();
        ApplyPalette("Light");
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) _lastMaximized = WindowState == WindowState.Maximized; };
        SessionTabs.SelectedItem = WelcomeTab;
        WorkspaceStatus.Text = "就绪";
        _timer.Tick += async (_, _) => await RefreshAsync();
        Closed += (_, _) => Stop();
    }
    public void Begin() { _timer.Start(); _ = RefreshAsync(); }
    private void RestorePlacement()
    {
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (_settingsDirectory == null || WindowPlacementStore.Load(_settingsDirectory) is not { } saved) return;
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = WindowPlacementStore.Fit(saved, desktop, MinWidth, MinHeight);
        Width = bounds.Width; Height = bounds.Height; Left = bounds.Left; Top = bounds.Top;
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (saved.Maximized) { WindowState = WindowState.Maximized; _lastMaximized = true; }
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        SavePlacement();
        if (CloseToTrayEnabled)
        {
            e.Cancel = true; IsInBackground = true; Hide(); HiddenToTray?.Invoke();
        }
    }
    private void SavePlacement()
    {
        if (!IsLoaded || _settingsDirectory == null) return;
        try
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (bounds.IsEmpty) return;
            WindowPlacementStore.Save(_settingsDirectory, new(bounds.Width, bounds.Height, bounds.Left, bounds.Top, _lastMaximized));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { WorkspaceStatus.Text = "窗口状态未保存：" + ex.Message; }
    }
    public void Stop() { _timer.Stop(); foreach (var item in _tabs.Values) item.View.StopPolling(); }
    private static JsonElement Element(object? value) => value is JsonElement e ? e : Wire.Element(value);
    private async Task<RpcResponse> CallAsync(string method, object? values = null)
    {
        var response = await _call(RpcRequest.Create(method, values));
        if (!response.Ok) throw new BrokerException(response.Code, response.Message);
        return response;
    }
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var response = await CallAsync("sessions");
            var sessions = Element(response.Result).Deserialize<WorkspaceSession[]>(Wire.Json)!;
            foreach (var id in _tabs.Keys.Except(sessions.Select(s => s.Id)).ToArray())
            { _tabs[id].View.StopPolling(); SessionTabs.Items.Remove(_tabs[id].Tab); _tabs.Remove(id); }
            foreach (var session in sessions)
            {
                if (!_tabs.ContainsKey(session.Id)) await AddTabAsync(session);
                var item = _tabs[session.Id];
                string name = session.Name.Length > 0 ? session.Name : session.Snapshot.Config.Username.Length > 0 ? session.Snapshot.Config.Host : "新连接";
                item.Label.Text = $"{name} · {session.Snapshot.State}";
                item.Tab.ToolTip = $"{session.Snapshot.Config.Host}:{session.Snapshot.Config.Port}\n本机端口：{session.Snapshot.LocalTelnetPort}";
                item.View.SetName(session.Name);
            }
            var history = Element((await CallAsync("history")).Result);
            var preferences = history.GetProperty("preferences").Deserialize<UserPreferences>(Wire.Json)!;
            WelcomeHistory.Text = $"已保存 {preferences.History.Length} 个历史目标";
            foreach (var item in _tabs.Values) item.View.UpdateHistory(preferences.History);
            string failure = history.GetProperty("failure").GetString() ?? "";
            WorkspaceStatus.Text = failure.Length > 0 ? failure : $"{sessions.Length} 个目标 · {sessions.Count(s => s.Snapshot.State == "connected")} 个已连接";
            if (_palette != preferences.Palette) ApplyPalette(preferences.Palette);
        }
        catch (Exception ex) { WorkspaceStatus.Text = "工作区：" + ex.Message; }
        finally { _refreshing = false; }
    }
    private async Task AddTabAsync(WorkspaceSession session)
    {
        var view = new SessionView(session.Pipe, request => RpcClient.CallAsync(session.Pipe, request, 150000));
        await view.RefreshAsync();
        await view.AttachWorkspaceAsync(session.Id, session.Name, _call);
        view.PaletteRequested += PaletteRequested;
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
        var close = new Button { Content = "×", Style = (Style)FindResource("QuietButton"), MinHeight = 22, MinWidth = 22, Padding = new Thickness(3, 0, 3, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "关闭此目标（会断开此目标的 SSH 与 Telnet）" };
        close.Click += async (_, e) =>
        {
            e.Handled = true;
            if (view.View.State is "connected" or "connecting" && MessageBox.Show(this, "关闭此标签会断开该设备的 SSH 和 Telnet，继续？", "关闭连接", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            try { await CallAsync("session-close", new { id = session.Id }); await RefreshAsync(); }
            catch (Exception ex) { WorkspaceStatus.Text = ex.Message; }
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal }; header.Children.Add(label); header.Children.Add(close);
        var tab = new TabItem { Header = header, Content = view };
        _tabs.Add(session.Id, (tab, view, label));
        SessionTabs.Items.Insert(SessionTabs.Items.Count - 1, tab);
        if (session.Snapshot.Config.Username.Length == 0) view.BlankNewHost();
        view.SetPalette(_palette); view.BeginPolling();
    }
    public async Task<string> CreateAsync(string name = "")
    {
        var result = Element((await CallAsync("session-create", new { name })).Result).Deserialize<WorkspaceSession>(Wire.Json)!;
        while (_refreshing) await Task.Delay(20);
        await RefreshAsync();
        if (_tabs.TryGetValue(result.Id, out var item)) SessionTabs.SelectedItem = item.Tab;
        return result.Id;
    }
    private async void NewClick(object sender, RoutedEventArgs e)
    { try { await CreateAsync(); } catch (Exception ex) { WorkspaceStatus.Text = ex.Message; } }
    private void HelpClick(object sender, RoutedEventArgs e)
    {
        try { HelpDocument.Open(); }
        catch (Exception ex) { WorkspaceStatus.Text = ex.Message; }
    }
    private void ApplyPalette(string palette)
    {
        _paletteSync = true;
        try
        {
            _palette = ConnectionHistory.NormalizePalette(palette); AppTheme.Apply(_palette);
            GlobalThemeBox.SelectedValue = _palette;
            foreach (var item in _tabs.Values) item.View.SetPalette(_palette);
        }
        finally { _paletteSync = false; }
    }
    private void GlobalThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_paletteSync && GlobalThemeBox.SelectedValue is string palette) PaletteRequested(palette);
    }
    private async void PaletteRequested(string palette)
    {
        if (_paletteSync || palette == _palette) return;
        ApplyPalette(palette);
        try { await CallAsync("palette", new { value = palette }); }
        catch (Exception ex) { WorkspaceStatus.Text = ex.Message; }
    }
    public async Task<object> ProbeAsync()
    {
        await RefreshAsync();
        return new { ok = true, inBackground = IsInBackground, closeToTray = CloseToTrayEnabled, welcome = SessionTabs.SelectedItem == WelcomeTab, tabs = _tabs.Select(pair => new
        { id = pair.Key, label = pair.Value.Label.Text, state = pair.Value.View.View.State, host = pair.Value.View.View.Host,
            port = pair.Value.View.View.LocalTelnetPort, rx = pair.Value.View.View.RxBytes, binding = pair.Value.View.BindingProbe() }).ToArray(), palette = _palette };
    }
    public async Task<object> UiSmokeAsync()
    {
        await RefreshAsync();
        bool welcome = _tabs.Count == 0 && SessionTabs.SelectedItem == WelcomeTab;
        string first = await CreateAsync("开发设备");
        var a = _tabs[first];
        bool blank = a.View.IsBlankNewHost;
        var firstSession = Element((await CallAsync("sessions")).Result).Deserialize<WorkspaceSession[]>(Wire.Json)!.Single();
        var config = await RpcClient.CallAsync(firstSession.Pipe, RpcRequest.Create("configure", new { host = "vm-editor.example", username = "builder" }));
        if (!config.Ok) throw new InvalidOperationException(config.Message);
        await a.View.RefreshAsync(); a.View.SetProbeDraft("first-draft");
        string second = await CreateAsync("开发设备");
        var b = _tabs[second];
        var editor = Element(await b.View.EditorProbeAsync());
        SessionTabs.SelectedItem = a.Tab; await a.View.RefreshAsync();
        bool retained = a.View.HasProbeDraft("first-draft");
        SessionTabs.SelectedItem = b.Tab;
        bool switched = ReferenceEquals(((TabItem)SessionTabs.SelectedItem).Content, b.View);
        await CallAsync("session-close", new { id = first }); await RefreshAsync();
        bool independentClose = _tabs.Count == 1 && _tabs.ContainsKey(second);
        await CallAsync("palette", new { value = "Light" }); await RefreshAsync();
        bool palette = _palette == "Light";
        bool placement = ProbeWindowPlacement();
        return new { ok = welcome && blank && editor.GetProperty("ok").GetBoolean() && retained && switched && independentClose && palette && placement,
            welcome, blank, editor, retained, switched, independentClose, palette, placement };
    }
    private bool ProbeWindowPlacement()
    {
        if (_settingsDirectory == null) return false;
        var desired = new WindowPlacement(1240, 840, 60, 50, false);
        WindowPlacementStore.Save(_settingsDirectory, desired);
        var restored = new WorkspaceWindow("placement-probe", _call, _settingsDirectory);
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var expected = WindowPlacementStore.Fit(desired, desktop, MinWidth, MinHeight);
        bool normal = restored.Width == expected.Width && restored.Height == expected.Height && restored.Left == expected.Left && restored.Top == expected.Top;
        restored.Close();
        WindowPlacementStore.Save(_settingsDirectory, desired with { Maximized = true });
        restored = new WorkspaceWindow("placement-probe", _call, _settingsDirectory);
        bool maximized = restored.WindowState == WindowState.Maximized;
        restored.Close();
        return normal && maximized;
    }
    public async Task RenderPreviewAsync(string path, double width, double height)
    {
        var content = (FrameworkElement)Content;
        content.InvalidateMeasure();
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        content.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)width, (int)height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var background = new System.Windows.Media.DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle((System.Windows.Media.Brush)FindResource("CanvasBrush"), null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        using var output = System.IO.File.Create(path); encoder.Save(output);
    }
    public void PreviewTheme(string theme) => ApplyPalette(theme);
    public void SelectConnectedTarget()
    {
        var target = _tabs.Values.FirstOrDefault(item => item.View.View.State == "connected");
        if (target.Tab != null) SessionTabs.SelectedItem = target.Tab;
    }
}
