using System.IO;
using System.Text.Json;
using System.Windows;
using SshBroker.Core;

namespace SshBroker.Gui;

public partial class App : Application
{
    private BrokerRuntime? _runtime;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var options = CommandParser.ParseOptions(e.Args);
            if (options.Keys.Any(k => k is not ("pipe" or "probe-output" or "probe-seconds" or "probe-own" or "probe-form" or "probe-login")))
                throw new BrokerException("invalid_params", "GUI accepts --pipe, --probe-output and --probe-seconds.");
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
            var window = new MainWindow(pipe, call); MainWindow = window;
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
            else { window.Show(); window.BeginPolling(); }
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
        if (_runtime != null) Task.Run(async () => await _runtime.DisposeAsync()).GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
