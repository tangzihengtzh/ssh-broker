using System.IO;
using System.Text.Json;
using System.Windows;
using SshBroker.Core;

namespace SshBroker.Gui;

public partial class App : Application
{
    private BrokerRuntime? _runtime;
    private WorkspaceRuntime? _workspace;
    private TrayHost? _tray;
    private WorkspaceWindow? _mainWorkspace;
    private bool _exitRequested;
    private void ExitImmediately()
    {
        if (_mainWorkspace != null) _mainWorkspace.CloseToTrayEnabled = false;
        _tray?.Dispose(); _tray = null;
        Shutdown();
    }
    private async Task ExitFromTrayAsync(Func<RpcRequest, Task<RpcResponse>> call, WorkspaceWindow window)
    {
        if (_exitRequested) return;
        _exitRequested = true;
        try
        {
            var response = await call(RpcRequest.Create("sessions"));
            var sessions = response.Result is JsonElement element ? element.Deserialize<WorkspaceSession[]>(Wire.Json) : Wire.Element(response.Result).Deserialize<WorkspaceSession[]>(Wire.Json);
            if (sessions?.Any(s => s.Snapshot.State is "connected" or "connecting") == true)
            {
                window.ShowFromTray();
                if (MessageBox.Show(window, "退出程序将断开所有现有连接，是否继续？", "退出 SSH Broker", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            }
            if (_workspace == null) await call(RpcRequest.Create("shutdown"));
            ExitImmediately();
        }
        catch (Exception) { ExitImmediately(); }
        finally { _exitRequested = false; }
    }
    private async Task StartWorkspaceAsync(Dictionary<string, string> options)
    {
        string pipe = options.GetValueOrDefault("pipe", "SshBrokerReconnect");
        string directory = options.GetValueOrDefault("data-directory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SshBroker", "reconnect-preview", "workspace"));
        Func<RpcRequest, Task<RpcResponse>> call;
        try
        {
            var existing = await RpcClient.CallAsync(pipe, RpcRequest.Create("sessions"), 700);
            if (!existing.Ok) throw new InvalidOperationException("此管道不是多会话工作区，请指定新的 --pipe 名称。");
            if (!options.ContainsKey("probe-output"))
            {
                var shown = await RpcClient.CallAsync(pipe, RpcRequest.Create("show-window"), 2000);
                if (shown.Ok && Wire.Element(shown.Result).GetProperty("shown").GetBoolean()) { Shutdown(); return; }
            }
            call = request => RpcClient.CallAsync(pipe, request, 150000);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException)
        {
            _workspace = new(pipe, directory); _workspace.Start();
            if (!options.ContainsKey("probe-output"))
                _workspace.ShowWindowRequested += () => Dispatcher.BeginInvoke(() => _mainWorkspace?.ShowFromTray());
            call = request => _workspace.ExecuteAsync(request);
            _ = _workspace.Shutdown.ContinueWith(_ => Dispatcher.BeginInvoke(ExitImmediately));
        }
        AppTheme.Apply("Light");
        var window = new WorkspaceWindow(pipe, call, directory); MainWindow = window;
        if (options.TryGetValue("probe-output", out var output))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            bool trayProbe = options.GetValueOrDefault("probe-tray", "false") == "true";
            if (trayProbe)
            {
                _mainWorkspace = window;
                _tray = new TrayHost(window.ShowFromTray, () => { }, ExitImmediately);
                window.CloseToTrayEnabled = true;
            }
            if (options.GetValueOrDefault("probe-ui", "false") == "true")
            {
                if (_workspace == null) throw new InvalidOperationException("UI smoke requires an isolated owned workspace.");
                if (options.TryGetValue("preview-directory", out var preview))
                    await window.RenderPreviewAsync(Path.Combine(preview, "welcome.png"), 1332, 888);
                var report = await window.UiSmokeAsync();
                if (options.TryGetValue("preview-directory", out preview))
                {
                    foreach (string theme in ConnectionHistory.Palettes)
                    {
                        window.PreviewTheme(theme);
                        await window.RenderPreviewAsync(Path.Combine(preview, "session-" + theme + ".png"), 1332, 888);
                    }
                    window.PreviewTheme("Light");
                    await window.RenderPreviewAsync(Path.Combine(preview, "session.png"), 1332, 888);
                    await window.RenderPreviewAsync(Path.Combine(preview, "session-compact.png"), 1072, 688);
                }
                await File.WriteAllTextAsync(Path.GetFullPath(output), JsonSerializer.Serialize(report, Wire.Json));
                window.Stop(); Shutdown(); return;
            }
            var deadline = DateTime.UtcNow.AddSeconds(int.Parse(options.GetValueOrDefault("probe-seconds", "20")));
            window.Begin();
            bool capturedWelcome = false, capturedWorkspace = false;
            while (DateTime.UtcNow < deadline)
            {
                var report = await window.ProbeAsync();
                if (options.TryGetValue("screenshot-directory", out var screenshots))
                {
                    if (!capturedWelcome && window.ConnectedTargetCount == 0)
                    {
                        await window.RenderPreviewAsync(Path.Combine(screenshots, "welcome.png"), 1380, 920);
                        capturedWelcome = true;
                    }
                    if (!capturedWorkspace && window.ConnectedTargetCount >= 2 && File.Exists(Path.Combine(screenshots, "capture.ready")))
                    {
                        window.SelectConnectedTarget(); window.PreviewTheme("Dark");
                        await window.RenderPreviewAsync(Path.Combine(screenshots, "workspace.png"), 1380, 920);
                        capturedWorkspace = true;
                    }
                }
                if (trayProbe && !window.IsInBackground && window.ConnectedTargetCount >= 2)
                {
                    window.Close();
                    if (!window.IsInBackground) throw new InvalidOperationException("Close did not hide to tray.");
                    report = await window.ProbeAsync();
                }
                string path = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(report, Wire.Json)); File.Move(path + ".tmp", path, true);
                await Task.Delay(250);
            }
            window.Stop(); Shutdown();
        }
        else
        {
            _mainWorkspace = window;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _tray = new TrayHost(window.ShowFromTray,
                () => { try { HelpDocument.Open(); } catch (Exception ex) { MessageBox.Show(ex.Message, "使用帮助"); } },
                () => _ = ExitFromTrayAsync(call, window));
            window.CloseToTrayEnabled = true;
            window.Show(); window.Begin();
        }
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var options = CommandParser.ParseOptions(e.Args);
            if (options.Keys.Any(k => k is not ("pipe" or "probe-output" or "probe-seconds" or "probe-own" or "probe-form" or "probe-login" or "probe-workspace" or "probe-ui" or "probe-tray" or "preview-directory" or "screenshot-directory" or "data-directory")))
                throw new BrokerException("invalid_params", "GUI accepts --pipe, --probe-output and --probe-seconds.");
            if (!options.ContainsKey("probe-output") || options.ContainsKey("probe-workspace")) { await StartWorkspaceAsync(options); return; }
            string pipe = options.GetValueOrDefault("pipe", "SshBroker");
            Func<RpcRequest, Task<RpcResponse>> call;
            try
            {
                var existing = await RpcClient.CallAsync(pipe, RpcRequest.Create("status"), 700);
                if (!existing.Ok) throw new IOException("Existing service did not accept status.");
                call = request => RpcClient.CallAsync(pipe, request, 150000);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException)
            {
                if (options.ContainsKey("probe-output") && options.GetValueOrDefault("probe-own", "false") != "true") throw;
                _runtime = new(pipe); _runtime.Start();
                call = request => _runtime.Dispatcher.ExecuteAsync(request);
                _ = _runtime.Shutdown.ContinueWith(_ => Dispatcher.BeginInvoke(() => Shutdown()));
            }
            var window = new SessionView(pipe, call);
            if (options.TryGetValue("probe-output", out var output))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                if (options.GetValueOrDefault("probe-login", "false") == "true")
                {
                    if (_runtime == null) throw new InvalidOperationException("Login regression requires an isolated owned broker.");
                    var report = await window.LoginRegressionProbeAsync(
                        Environment.GetEnvironmentVariable("SSHBROKER_VM_HOST") ?? throw new InvalidOperationException("Missing probe host."),
                        Environment.GetEnvironmentVariable("SSHBROKER_VM_USER") ?? throw new InvalidOperationException("Missing probe user."),
                        Environment.GetEnvironmentVariable("SSHBROKER_VM_PASSWORD") ?? throw new InvalidOperationException("Missing probe password."));
                    await File.WriteAllTextAsync(Path.GetFullPath(output), JsonSerializer.Serialize(report, Wire.Json));
                    int loginSeconds = int.Parse(options.GetValueOrDefault("probe-seconds", "15"));
                    window.BeginPolling();
                    await Task.Delay(TimeSpan.FromSeconds(loginSeconds));
                    window.StopPolling(); Shutdown(); return;
                }
                if (options.GetValueOrDefault("probe-form", "false") == "true")
                {
                    if (_runtime == null) throw new InvalidOperationException("Form regression requires an isolated owned broker.");
                    var report = await window.FormRegressionProbeAsync();
                    await File.WriteAllTextAsync(Path.GetFullPath(output), JsonSerializer.Serialize(report, Wire.Json));
                    Shutdown(); return;
                }
                int seconds = int.Parse(options.GetValueOrDefault("probe-seconds", "30"), System.Globalization.CultureInfo.InvariantCulture);
                window.BeginPolling();
                var deadline = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < deadline)
                {
                    await window.RefreshAsync();
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    var report = window.BindingProbe();
                    var path = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(report, Wire.Json));
                    File.Move(path + ".tmp", path, true);
                    await Task.Delay(200);
                }
                window.StopPolling(); Shutdown();
            }
            else { throw new InvalidOperationException("Probe output required."); }
        }
        catch (Exception ex)
        {
            if (e.Args.Contains("--probe-output"))
            {
                int index = Array.IndexOf(e.Args, "--probe-output");
                if (index + 1 < e.Args.Length) await File.WriteAllTextAsync(e.Args[index + 1], JsonSerializer.Serialize(new { ok = false, error = ex.Message }, Wire.Json));
            }
            else MessageBox.Show(ex.Message, "SshBroker 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose(); _tray = null;
        if (_workspace != null) Task.Run(async () => await _workspace.DisposeAsync()).GetAwaiter().GetResult();
        if (_runtime != null) Task.Run(async () => await _runtime.DisposeAsync()).GetAwaiter().GetResult();
        base.OnExit(e);
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (_mainWorkspace != null) _mainWorkspace.CloseToTrayEnabled = false;
        base.OnSessionEnding(e);
    }
}
