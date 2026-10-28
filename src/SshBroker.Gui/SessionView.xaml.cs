using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SshBroker.Core;

namespace SshBroker.Gui;

public partial class SessionView : UserControl
{
    public SnapshotProjection View { get; } = new();
    private readonly Func<RpcRequest, Task<RpcResponse>> _call;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _refreshing, _applying, _draft, _syncing;
    private long _formRevision = -1, _cursor;
    private bool _connecting;
    private Func<RpcRequest, Task<RpcResponse>>? _workspace;
    private string _sessionId = "";
    private ConnectionProfile[] _profiles = [];
    private bool _choosingHistory;
    private int _hostEdit;
    private int _portEdit;
    private bool _suggesting;
    private Task _pendingHost = Task.CompletedTask;
    private bool _nameDraft, _settingName;
    public event Action<string>? PaletteRequested;
    public void SetName(string name)
    {
        if (_nameDraft || NameBox.IsKeyboardFocusWithin) return;
        _settingName = true; try { NameBox.Text = name; } finally { _settingName = false; }
    }
    private void NameEdited(object sender, TextChangedEventArgs e) { if (!_settingName) _nameDraft = true; }
    public void UpdateHistory(ConnectionProfile[] profiles)
    {
        if (_profiles.SequenceEqual(profiles)) return;
        _syncing = true;
        try { string text = HostBox.Text; _profiles = profiles; HostBox.ItemsSource = profiles; HostBox.Text = text; }
        finally { _syncing = false; }
    }
    public async Task AttachWorkspaceAsync(string id, string name, Func<RpcRequest, Task<RpcResponse>> workspace)
    {
        _workspace = workspace; _sessionId = id; SetName(name);
        var response = await workspace(RpcRequest.Create("history", new { exclude = id }));
        if (!response.Ok) { StatusLabel.Text = response.Message; return; }
        var data = ToElement(response.Result);
        var preferences = data.GetProperty("preferences").Deserialize<UserPreferences>(Wire.Json)!;
        _profiles = preferences.History;
        HostBox.ItemsSource = _profiles;
        SetPalette(preferences.Palette);
    }
    public SessionView(string pipe, Func<RpcRequest, Task<RpcResponse>> call)
    {
        _call = call;
        InitializeComponent(); DataContext = this;
        HostBox.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(HostEdited));
        PaletteBox.ItemsSource = ConnectionHistory.Palettes; SetPalette("Light");
        _poll.Tick += async (_, _) => await RefreshAsync();

    }
    public void BeginPolling() { _poll.Start(); _ = RefreshAsync(); }
    public void StopPolling() => _poll.Stop();
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var status = await _call(RpcRequest.Create("status")); Apply(status.Snapshot);
            var output = await _call(RpcRequest.Create("output", new { after = _cursor, limit = 60 }));
            Apply(output.Snapshot);
            if (output.Ok)
            {
                var page = ToElement(output.Result).Deserialize<OutputPage>(Wire.Json)!; _cursor = page.Next;
                if (PauseBox.IsChecked != true)
                {
                    if (page.LostHistory) MonitorBox.AppendText("[显示历史已裁剪]\n");
                    foreach (var entry in page.Entries)
                    {
                        string text = entry.Direction == "event" ? entry.Text : Escape(entry.Text);
                        MonitorBox.AppendText($"{entry.Time.LocalDateTime:HH:mm:ss.fff} [{entry.Direction}] {entry.Client} {text}\n");
                    }
                    if (MonitorBox.Text.Length > 200000) MonitorBox.Text = MonitorBox.Text[^120000..];
                    if (page.Entries.Length > 0) MonitorBox.ScrollToEnd();
                }
            }
        }
        catch (Exception ex) { StatusLabel.Text = "控制连接异常：" + ex.Message; }
        finally { _refreshing = false; }
    }
    private void Apply(BrokerSnapshot snapshot)
    {
        _syncing = true;
        try
        {
            if (!View.Apply(snapshot)) return;
            if (_formRevision != snapshot.ConfigRevision)
            {
                var c = snapshot.Config;
                HostBox.Text = c.Host; PortBox.Text = c.Port.ToString(CultureInfo.InvariantCulture); UserBox.Text = c.Username;
                AuthBox.SelectedValue = c.Authentication; KeyBox.Text = c.PrivateKeyPath; FingerprintBox.Text = c.HostKeyFingerprint;
                TelnetEnabledBox.IsChecked = c.TelnetEnabled; TelnetPortBox.Text = c.TelnetPort.ToString(CultureInfo.InvariantCulture);
                AutoReconnectBox.IsChecked = c.ReconnectAttempts > 0;
                ReconnectAttemptsBox.Text = Math.Max(1, c.ReconnectAttempts).ToString(CultureInfo.InvariantCulture);
                ReconnectDelayBox.Text = (c.ReconnectDelayMs / 1000m).ToString(CultureInfo.InvariantCulture);
                LogRootBox.Text = c.LogRoot; ColumnsBox.Text = c.Columns.ToString(CultureInfo.InvariantCulture); RowsBox.Text = c.Rows.ToString(CultureInfo.InvariantCulture);
                // Keep this window's masked input after its own successful submit.
                // External updates must discard stale local secrets before a later submit.
                if (!_applying) { PasswordInput.Clear(); PassphraseInput.Clear(); }
                PasswordLabel.Text = snapshot.HasPassword ? "密码（已设置）" : "密码";
                _formRevision = snapshot.ConfigRevision; _draft = false;
                DraftLabel.Text = "配置已保存";
            }
            bool editable = snapshot.State is "disconnected" or "faulted";
            ConnectionFields.IsEnabled = editable; ApplyButton.IsEnabled = editable && !_applying; ConnectButton.IsEnabled = editable && !_applying;
            LogRootBox.IsEnabled = editable;
        }
        finally { _syncing = false; }
    }
    private static JsonElement ToElement(object? value) => value is JsonElement element ? element : Wire.Element(value);
    private async Task<bool> ExecuteAsync(RpcRequest request)
    {
        try
        {
            var response = await _call(request); Apply(response.Snapshot);
            StatusLabel.Text = response.Ok ? request.Method switch
            { "connect" => "已连接", "disconnect" => "已断开", "configure" => "配置已保存", "send" => "已发送", "resize" => "尺寸已更新", _ => "已更新" } : response.Code switch
            {
                "host_key_unknown" => "尚未信任服务器主机密钥，与密码无关。请核对观测指纹，点击“核对后使用此指纹”，然后再次连接。密码已保留。",
                "host_key_mismatch" => "服务器主机密钥与受信任指纹不一致，请确认服务器身份后再更新指纹。",
                "authentication_failed" => "服务器认证失败，请检查用户名、密码或私钥。",
                _ => $"{response.Code}: {response.Message}"
            };
            return response.Ok;
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; return false; }
    }
    private async Task<bool> ApplyDraftAsync()
    {
        try
        {
            var values = new Dictionary<string, object>
            {
                ["host"] = HostBox.Text.Trim(), ["port"] = int.Parse(PortBox.Text, CultureInfo.InvariantCulture),
                ["username"] = UserBox.Text, ["authentication"] = AuthBox.SelectedValue?.ToString() ?? "password",
                ["privateKeyPath"] = KeyBox.Text, ["hostKeyFingerprint"] = FingerprintBox.Text.Trim(),
                ["telnetEnabled"] = TelnetEnabledBox.IsChecked == true, ["telnetPort"] = int.Parse(TelnetPortBox.Text, CultureInfo.InvariantCulture),
                ["reconnectAttempts"] = AutoReconnectBox.IsChecked == true ? int.Parse(ReconnectAttemptsBox.Text, CultureInfo.InvariantCulture) : 0,
                ["reconnectDelayMs"] = checked((int)(decimal.Parse(ReconnectDelayBox.Text, CultureInfo.InvariantCulture) * 1000)),
                ["logRoot"] = LogRootBox.Text, ["columns"] = int.Parse(ColumnsBox.Text, CultureInfo.InvariantCulture),
                ["rows"] = int.Parse(RowsBox.Text, CultureInfo.InvariantCulture), ["expectedRevision"] = View.Revision
            };
            if (PasswordInput.Password.Length > 0) values["password"] = PasswordInput.Password;
            if (PassphraseInput.Password.Length > 0) values["keyPassphrase"] = PassphraseInput.Password;
            if (HostBox.Text.Trim() != View.Host || int.Parse(PortBox.Text, CultureInfo.InvariantCulture) != View.Port)
            {
                values.TryAdd("password", ""); values.TryAdd("keyPassphrase", "");
            }
            if (_workspace != null)
            {
                var renamed = await _workspace(RpcRequest.Create("session-rename", new { id = _sessionId, name = NameBox.Text }));
                if (!renamed.Ok) { StatusLabel.Text = renamed.Message; return false; }
                _nameDraft = false;
            }
            _applying = true;
            return await ExecuteAsync(RpcRequest.Create("configure", values));
        }
        catch (Exception e) { StatusLabel.Text = "配置错误：" + e.Message; return false; }
        finally { _applying = false; }
    }
    private void DraftChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || DraftLabel == null) return;
        _draft = true; DraftLabel.Text = "未保存的更改";
    }
    private void PasswordChanged(object sender, RoutedEventArgs e) => DraftChanged(sender, e);
    private async void NameLostFocus(object sender, RoutedEventArgs e)
    {
        if (_workspace == null) return;
        try { var response = await _workspace(RpcRequest.Create("session-rename", new { id = _sessionId, name = NameBox.Text })); if (!response.Ok) StatusLabel.Text = response.Message; else _nameDraft = false; }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
    private void TelnetPortEdited(object sender, TextChangedEventArgs e)
    { if (!_syncing && !_choosingHistory && !_suggesting) _portEdit++; DraftChanged(sender, e); }
    private void HostEdited(object sender, TextChangedEventArgs e)
    {
        if (_syncing || _choosingHistory) return;
        DraftChanged(sender, e);
        int edit = ++_hostEdit;
        if (!HostBox.Text.Trim().Equals(View.Host, StringComparison.OrdinalIgnoreCase)) { PasswordInput.Clear(); PassphraseInput.Clear(); }
        if (_workspace == null) return;
        _pendingHost = UpdateHostAsync(edit, _portEdit);
    }
    private async Task UpdateHostAsync(int edit, int portEdit)
    {
        await Task.Delay(350);
        if (edit != _hostEdit || _syncing || View.State is "connected" or "connecting") return;
        string host = HostBox.Text.Trim();
        if (host.Length == 0) return;
        try
        {
            int port = int.TryParse(PortBox.Text, out var sshPort) ? sshPort : 22;
            var response = await _workspace!(RpcRequest.Create("history", new { host, port, exclude = _sessionId }));
            if (edit != _hostEdit || !response.Ok) return;
            var data = ToElement(response.Result);
            if (_portEdit == portEdit)
            {
                _suggesting = true;
                try { TelnetPortBox.Text = data.GetProperty("suggestedPort").GetInt32().ToString(CultureInfo.InvariantCulture); }
                finally { _suggesting = false; }
            }
            // A pin from a different endpoint must not follow an edited host.
            FingerprintBox.Text = HostTrustStore.Load(host, port);
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
    private void HistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || HostBox.SelectedItem is not ConnectionProfile item) return;
        _choosingHistory = true; ++_hostEdit;
        try
        {
            HostBox.Text = item.Host; PortBox.Text = item.Port.ToString(CultureInfo.InvariantCulture);
            UserBox.Text = item.Username; NameBox.Text = item.Name;
            TelnetPortBox.Text = item.TelnetPort.ToString(CultureInfo.InvariantCulture);
            FingerprintBox.Text = HostTrustStore.Load(item.Host, item.Port);
            PasswordInput.Clear(); PassphraseInput.Clear(); DraftChanged(sender, e);
        }
        finally { _choosingHistory = false; }
    }
    public void SetPalette(string palette)
    {
        palette = ConnectionHistory.NormalizePalette(palette);
        var colors = palette switch
        {
            "Dark" => ("#1A202B", "#DFE6F3"),
            "Warm" => ("#FAF6EF", "#514435"),
            "Blue" => ("#EFF6FD", "#294D6E"),
            _ => ("#F8FAFD", "#2B374D")
        };
        MonitorBox.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(colors.Item1)!;
        MonitorBox.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(colors.Item2)!;
        PaletteBox.SelectedItem = palette;
    }
    private void PaletteChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MonitorBox == null || PaletteBox.SelectedItem is not string palette) return;
        SetPalette(palette); PaletteRequested?.Invoke(palette);
    }
    private void OpenLogFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = System.IO.Path.GetFullPath(LogRootBox.Text);
            System.IO.Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusLabel.Text = "无法打开日志目录：" + ex.Message; }
    }
    public void BlankNewHost()
    {
        _syncing = true;
        try { HostBox.Text = ""; UserBox.Text = ""; PasswordInput.Clear(); }
        finally { _syncing = false; }
        _draft = true; DraftLabel.Text = "新连接";
    }
    public bool IsBlankNewHost => HostBox.Text.Length == 0 && PasswordInput.Password.Length == 0;
    public void SetProbeDraft(string value) { SendBox.Text = value; PasswordInput.Password = value; }
    public bool HasProbeDraft(string value) => SendBox.Text == value && PasswordInput.Password == value;
    public async Task<object> EditorProbeAsync()
    {
        if (_profiles.Length == 0) throw new InvalidOperationException("History fixture missing.");
        var profile = _profiles[0];
        HostBox.SelectedItem = profile;
        await Task.Delay(50);
        bool selected = HostBox.Text == profile.Host && NameBox.Text == profile.Name && UserBox.Text == profile.Username && TelnetPortBox.Text == profile.TelnetPort.ToString(CultureInfo.InvariantCulture) && PasswordInput.Password.Length == 0;
        HostBox.Text = "new-editor.example"; TelnetPortBox.Text = "7891";
        await _pendingHost;
        bool manual = TelnetPortBox.Text == "7891";
        HostBox.Text = "another-editor.example";
        PasswordInput.Password = "typed-after-host";
        await _pendingHost;
        bool suggestion = int.TryParse(TelnetPortBox.Text, out int value) && value > 0 && !_profiles.Any(p => p.TelnetPort == value);
        bool passwordRetained = PasswordInput.Password == "typed-after-host";
        var colors = new HashSet<string>();
        foreach (string palette in ConnectionHistory.Palettes) { SetPalette(palette); colors.Add(MonitorBox.Background.ToString()); }
        bool palettes = colors.Count == ConnectionHistory.Palettes.Length;
        return new { ok = selected && manual && suggestion && passwordRetained && palettes, selected, manual, suggestion, passwordRetained, palettes };
    }
    private void AuthChanged(object sender, SelectionChangedEventArgs e) => DraftChanged(sender, e);
    private async void ApplyClick(object sender, RoutedEventArgs e) => await ApplyDraftAsync();
    private void TrustFingerprintClick(object sender, RoutedEventArgs e)
    {
        var snapshot = View.Snapshot;
        if (snapshot.ObservedHostKey.Length == 0 || HostBox.Text.Trim() != snapshot.Config.Host || PortBox.Text.Trim() != snapshot.Config.Port.ToString(CultureInfo.InvariantCulture))
        {
            StatusLabel.Text = "请先连接当前主机以获取其观测指纹，再核对。";
            return;
        }
        if (MessageBox.Show(Window.GetWindow(this), $"请先与服务器管理员或已有可信 SSH 记录核对：\n\n{snapshot.Config.Host}:{snapshot.Config.Port}\n{snapshot.ObservedHostKey}\n\n确认此指纹属于目标服务器并填入？", "确认服务器主机密钥", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        FingerprintBox.Text = snapshot.ObservedHostKey;
        StatusLabel.Text = "已填入核对后的指纹，请点击连接 SSH；密码已保留。";
    }

    public async Task<object> FormRegressionProbeAsync()
    {
        await RefreshAsync();
        HostBox.Text = "regression.invalid"; UserBox.Text = "test";
        PasswordInput.Password = "probe-secret";
        bool applied = await ApplyDraftAsync();
        await RefreshAsync();
        bool retained = PasswordInput.Password == "probe-secret" && View.Snapshot.HasPassword;
        var external = await _call(RpcRequest.Create("configure", new { username = "external", password = "external-secret" }));
        Apply(external.Snapshot);
        bool cleared = PasswordInput.Password.Length == 0 && View.Snapshot.HasPassword;
        return new { ok = applied && retained && external.Ok && cleared, applied, retained, cleared };
    }
    private async void ConnectClick(object sender, RoutedEventArgs e) => await ConnectFromFormAsync();
    private async Task ConnectFromFormAsync()
    {
        if (_connecting) return;
        _connecting = true;
        try
        {
            await _pendingHost;
            if (int.TryParse(PortBox.Text, out int port) && FingerprintBox.Text.Length == 0)
                FingerprintBox.Text = HostTrustStore.Load(HostBox.Text.Trim(), port);
            if ((_draft || PasswordInput.Password.Length > 0 || PassphraseInput.Password.Length > 0) && !await ApplyDraftAsync()) return;
            bool connected = await ExecuteAsync(RpcRequest.Create("connect"));
            if (!connected && View.Snapshot.LastError?.Code == "host_key_unknown")
            {
                var s = View.Snapshot;
                bool trusted = HostTrustStore.MatchesOpenSsh(s.Config.Host, s.Config.Port, s.ObservedHostKey);
                if (!trusted)
                    trusted = MessageBox.Show(Window.GetWindow(this), $"首次连接服务器 {s.Config.Host}:{s.Config.Port}。\n\n服务器身份：{s.ObservedHostKey}\n\n是否信任并记住此服务器？以后连接无需再次确认。", "首次 SSH 连接", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                if (!trusted) { StatusLabel.Text = "未信任服务器，连接已取消。密码保留，可再次连接。"; return; }
                FingerprintBox.Text = s.ObservedHostKey;
                if (!await ApplyDraftAsync()) return;
                connected = await ExecuteAsync(RpcRequest.Create("connect"));
            }
            if (connected)
            {
                var s = View.Snapshot;
                try { HostTrustStore.Save(s.Config.Host, s.Config.Port, s.ObservedHostKey); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                { StatusLabel.Text = "已连接，但服务器身份记录保存失败：" + ex.Message; }
            }
        }
        finally { _connecting = false; }
    }
    public async Task<object> LoginRegressionProbeAsync(string host, string username, string password)
    {
        await RefreshAsync();
        HostBox.Text = host; UserBox.Text = username; PasswordInput.Password = password;
        FingerprintBox.Text = "";
        await ConnectFromFormAsync();
        bool firstConnected = View.State == "connected";
        bool retained = PasswordInput.Password == password;
        bool remembered = HostTrustStore.Load(host, 22) == View.Snapshot.ObservedHostKey && View.Snapshot.ObservedHostKey.Length > 0;
        var result = new { ok = firstConnected && retained && remembered, firstConnected, retained, remembered, port = View.Snapshot.LocalTelnetPort };
        return result;
    }
    private async void DisconnectClick(object sender, RoutedEventArgs e) => await ExecuteAsync(RpcRequest.Create("disconnect"));
    private async void ReconnectClick(object sender, RoutedEventArgs e)
    {
        if (_connecting) return;
        if (await ExecuteAsync(RpcRequest.Create("disconnect"))) ConnectClick(sender, e);
    }
    private async void ResizeClick(object sender, RoutedEventArgs e)
    {
        try { await ExecuteAsync(RpcRequest.Create("resize", new { columns = int.Parse(ColumnsBox.Text, CultureInfo.InvariantCulture), rows = int.Parse(RowsBox.Text, CultureInfo.InvariantCulture) })); }
        catch (FormatException) { StatusLabel.Text = "PTY 尺寸必须是整数。"; }
    }
    private async void PolicyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || PolicySelector?.SelectedValue is not string policy || policy == View.Policy) return;
        await ExecuteAsync(RpcRequest.Create("policy", new { value = policy }));
    }
    private async void SendClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var args = new List<string> { "send", HexBox.IsChecked == true ? "--hex" : "--text", SendBox.Text, "--client", "gui" };
            if (CrBox.IsChecked == true) args.Add("--cr");
            if (await ExecuteAsync(CommandParser.Parse(args))) SendBox.Clear();
        }
        catch (BrokerException ex) { StatusLabel.Text = ex.Message; }
    }
    private void ClearClick(object sender, RoutedEventArgs e) => MonitorBox.Clear();
    private void ClientsGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is DataGrid grid && grid.Columns.Count > 0)
            grid.Columns[0].Width = new DataGridLength(Math.Max(100, e.NewSize.Width - 254));
    }
    private static string Escape(string text)
    {
        var output = new StringBuilder();
        foreach (var c in text) output.Append(c switch { '\r' => "\\r", '\n' => "\\n", '\t' => "\\t", _ when char.IsControl(c) => $"\\x{(int)c:X2}", _ => c.ToString() });
        return output.ToString();
    }
    public object BindingProbe()
    {
        var mismatches = new List<string>();
        void Check(string name, string? actual, string expected) { if (actual != expected) mismatches.Add(name); }
        Check("host", HostValue.Text, View.Host); Check("port", PortValue.Text, View.Port.ToString(CultureInfo.InvariantCulture));
        Check("username", UserValue.Text, View.Username); Check("state", StateValue.Text, View.State);
        Check("revision", RevisionValue.Text, View.Revision.ToString(CultureInfo.InvariantCulture)); Check("policy", PolicySelector.SelectedValue?.ToString(), View.Policy);
        Check("hostEditor", HostBox.Text, View.Host); Check("portEditor", PortBox.Text, View.Port.ToString(CultureInfo.InvariantCulture));
        Check("fingerprintEditor", FingerprintBox.Text, View.Snapshot.Config.HostKeyFingerprint);
        Check("usernameEditor", UserBox.Text, View.Username);
        Check("authEditor", AuthBox.SelectedValue?.ToString(), View.Snapshot.Config.Authentication);
        Check("reconnectAttemptsEditor", ReconnectAttemptsBox.Text, Math.Max(1, View.Snapshot.Config.ReconnectAttempts).ToString(CultureInfo.InvariantCulture));
        Check("reconnectDelayEditor", ReconnectDelayBox.Text, (View.Snapshot.Config.ReconnectDelayMs / 1000m).ToString(CultureInfo.InvariantCulture));
        if (AutoReconnectBox.IsChecked != (View.Snapshot.Config.ReconnectAttempts > 0)) mismatches.Add("autoReconnectEditor");
        Check("privateKeyEditor", KeyBox.Text, View.Snapshot.Config.PrivateKeyPath);
        Check("columnsEditor", ColumnsBox.Text, View.Snapshot.Config.Columns.ToString(CultureInfo.InvariantCulture));
        Check("rowsEditor", RowsBox.Text, View.Snapshot.Config.Rows.ToString(CultureInfo.InvariantCulture));
        Check("telnetPortEditor", TelnetPortBox.Text, View.Snapshot.Config.TelnetPort.ToString(CultureInfo.InvariantCulture));
        Check("logRootEditor", LogRootBox.Text, View.Snapshot.Config.LogRoot);
        if (TelnetEnabledBox.IsChecked != View.Snapshot.Config.TelnetEnabled) mismatches.Add("telnetEnabledEditor");
        return new { ok = mismatches.Count == 0, mismatches, View.Revision, View.ConfigRevision, View.State,
            View.Host, View.Port, View.Username, View.Policy, View.RxBytes, View.TxBytes,
            displayedHost = HostValue.Text, displayedPort = PortValue.Text, displayedPolicy = PolicySelector.SelectedValue?.ToString(),
            displayedState = StateValue.Text, displayedRevision = RevisionValue.Text,
            displayedOutputTail = MonitorBox.Text.Length > 8192 ? MonitorBox.Text[^8192..] : MonitorBox.Text };
    }
}
