// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.LightClient.Test;

public class HostTests
{
    private const string ChromeOrigin = "chrome-extension://nkbihfbeogaeaoehlefnkodbefgpgknn";
    private const string FirefoxOrigin = "moz-extension://12345678-1234-1234-1234-123456789abc";

    [Test]
    public void P2p_ports_preserve_defaults()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        Assert.That(LightClientHost.ReadPorts(configuration), Is.EqualTo((9050, 9051, 30307)));
    }

    [Test]
    public void P2p_ports_accept_command_line_overrides([Values(0, 29051)] int quicPort)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddCommandLine([
            "--beacon-port", "29050", "--beacon-quic-port", quicPort.ToString(), "--execution-port", "30317",
        ]).Build();

        Assert.That(LightClientHost.ReadPorts(configuration), Is.EqualTo((29050, quicPort, 30317)));
    }

    [Test]
    public void P2p_ports_accept_valid_boundaries(
        [Values("beacon-port", "beacon-quic-port", "execution-port")] string option, [Values(1, 65535)] int port)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddCommandLine([$"--{option}", port.ToString()]).Build();

        (int beacon, int quic, int execution) = LightClientHost.ReadPorts(configuration);

        Assert.That(option switch { "beacon-port" => beacon, "beacon-quic-port" => quic, _ => execution }, Is.EqualTo(port));
    }

    [Test]
    public void P2p_ports_reject_out_of_range_values(
        [Values("beacon-port", "beacon-quic-port", "execution-port")] string option, [Values(-1, 65536)] int port)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddCommandLine([$"--{option}", port.ToString()]).Build();

        Assert.That(() => LightClientHost.ReadPorts(configuration), Throws.TypeOf<ArgumentOutOfRangeException>()
            .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo(option));
    }

    [Test]
    public void P2p_ports_reject_zero_tcp_ports([Values("beacon-port", "execution-port")] string option)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddCommandLine([$"--{option}", "0"]).Build();

        Assert.That(() => LightClientHost.ReadPorts(configuration), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase("beacon-port", "30307")]
    [TestCase("beacon-quic-port", "9050")]
    [TestCase("beacon-quic-port", "30307")]
    public void P2p_ports_reject_overlapping_listeners(string option, string port)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddCommandLine([$"--{option}", port]).Build();

        Assert.That(() => LightClientHost.ReadPorts(configuration), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void P2p_ports_reject_non_integer_values([Values("beacon-port", "beacon-quic-port", "execution-port")] string option)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddCommandLine([$"--{option}", "not-a-port"]).Build();

        Assert.That(() => LightClientHost.ReadPorts(configuration), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    [NonParallelizable]
    public async Task Ctrl_C_cancels_checkpoint_fetch_while_the_status_listener_is_running()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "NethermindLightClientShutdownTests", Guid.NewGuid().ToString("N"));
        using TcpListener provider = new(IPAddress.Loopback, 0);
        provider.Start();
        int port = ((IPEndPoint)provider.LocalEndpoint).Port;
        using TcpListener reservation = new(IPAddress.Loopback, 0);
        reservation.Start();
        string rpcUrl = $"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}";
        reservation.Stop();
        string assembly = typeof(LightClientHost).Assembly.Location;
        string application = Path.GetDirectoryName(assembly)!;
        ProcessStartInfo start = new("dotnet") { UseShellExecute = OperatingSystem.IsWindows() };
        if (OperatingSystem.IsWindows()) start.WindowStyle = ProcessWindowStyle.Hidden;
        foreach (string argument in new[]
        {
            "exec", "--runtimeconfig", Path.Combine(application, "Nethermind.LightClient.runtimeconfig.json"),
            "--depsfile", Path.Combine(application, "Nethermind.LightClient.deps.json"), assembly,
            "--checkpoint-url", $"https://127.0.0.1:{port}", "--urls", rpcUrl, "--data-dir", directory,
        }) start.ArgumentList.Add(argument);
        using Process client = Process.Start(start)!;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            using TcpClient connection = await provider.AcceptTcpClientAsync(timeout.Token);
            using HttpClient rpc = new();
            using JsonDocument progress = JsonDocument.Parse(await rpc.GetStringAsync(rpcUrl + "/status", timeout.Token));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(progress.RootElement.GetProperty("startupStage").GetString(), Is.EqualTo("checkpoint"));
                Assert.That(progress.RootElement.GetProperty("rpcInitialized").GetBoolean(), Is.False);
                Assert.That(progress.RootElement.GetProperty("latest").ValueKind, Is.EqualTo(JsonValueKind.Null));
            }
            using CancellationTokenSource shutdownDeadline = new(TimeSpan.FromSeconds(5));
            await SendInterruptAsync(client.Id, shutdownDeadline.Token);
            await client.WaitForExitAsync(shutdownDeadline.Token);
            Assert.That(client.ExitCode, Is.Zero, "Ctrl+C during checkpoint selection must unwind normally.");
        }
        finally
        {
            if (!client.HasExited) client.Kill(entireProcessTree: true);
            await client.WaitForExitAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestCase(null, null, 200)]
    [TestCase("local", null, 200)]
    [TestCase("https://wallet.example", null, 403)]
    [TestCase(null, "attacker.example", 403)]
    [TestCase("http://attacker.example", "attacker.example", 403)]
    [TestCase(ChromeOrigin, null, 200)]
    [TestCase(FirefoxOrigin, null, 200)]
    [TestCase(ChromeOrigin, "attacker.example", 403)]
    [TestCase("null", null, 403)]
    [TestCase("chrome-extension://abc", null, 403)]
    [TestCase("chrome-extension://nkbihfbeogaeaoehlefnkodbefgpgknz", null, 403)]
    [TestCase("https://nkbihfbeogaeaoehlefnkodbefgpgknn", null, 403)]
    [TestCase(ChromeOrigin + ".attacker.example", null, 403)]
    [TestCase(ChromeOrigin + ":8545", null, 403)]
    [TestCase("chrome-extension://user@nkbihfbeogaeaoehlefnkodbefgpgknn", null, 403)]
    [TestCase(ChromeOrigin + "/", null, 403)]
    [TestCase(ChromeOrigin + "?query", null, 403)]
    [TestCase(ChromeOrigin + "#fragment", null, 403)]
    [TestCase("moz-extension://not-a-uuid", null, 403)]
    [TestCase("moz-extension://12345678123412341234123456789abc", null, 403)]
    [TestCase(ChromeOrigin + ", " + FirefoxOrigin, null, 403)]
    public async Task Rpc_origin_and_host_policy_rejects_cross_site_and_rebinding_requests(
        string? requestOrigin, string? requestHost, int expectedStatus)
    {
        await using WebApplication app = CreateApp("http://127.0.0.1:0");
        LightClientHost.ConfigureRequestPolicy(app, "http://127.0.0.1:0");
        app.MapGet("/", () => "ok");
        app.MapGet("/ws", () => "ok");
        app.MapGet("/status", () => "ok");
        await app.StartAsync();
        using HttpClient client = new();
        foreach (string path in new[] { "/", "/ws", "/status" })
        {
            using HttpRequestMessage request = new(HttpMethod.Get, app.Urls.Single() + path);
            if (requestOrigin is not null) request.Headers.Add("Origin", requestOrigin == "local" ? app.Urls.Single() : requestOrigin);
            if (requestHost is not null) request.Headers.Host = requestHost;
            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.That((int)response.StatusCode, Is.EqualTo(expectedStatus), path);
        }
    }

    [TestCase(ChromeOrigin)]
    [TestCase(FirefoxOrigin)]
    public async Task Wallet_extension_can_preflight_and_fetch_chain_id(string origin)
    {
        await using WebApplication app = CreateApp("http://127.0.0.1:0");
        LightClientHost.ConfigureRequestPolicy(app, "http://127.0.0.1:0");
        using VerifiedRpc rpc = new(null!, () => throw new AssertionException("Unexpected head read."), 1);
        app.MapPost("/", (HttpContext context) => RpcEndpoint.HandleAsync(context, rpc.InvokeAsync, NullLogger.Instance));
        await app.StartAsync();
        using HttpClient client = new();
        using HttpRequestMessage preflight = new(HttpMethod.Options, app.Urls.Single());
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type,authorization");
        using HttpResponseMessage options = await client.SendAsync(preflight);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(options.Headers.GetValues("Access-Control-Allow-Origin"), Is.EqualTo(new[] { origin }));
            Assert.That(options.Headers.GetValues("Vary"), Does.Contain("Origin"));
            Assert.That(options.Headers.GetValues("Access-Control-Allow-Methods").Single().Split(','), Does.Contain("POST"));
            Assert.That(options.Headers.GetValues("Access-Control-Allow-Headers").Single().Split(','),
                Is.EquivalentTo(new[] { "content-type", "authorization" }));
            Assert.That(options.Headers.Contains("Access-Control-Allow-Credentials"), Is.False);
            Assert.That(options.Headers.Contains("Access-Control-Allow-Private-Network"), Is.False);
        }

        using HttpRequestMessage request = new(HttpMethod.Post, app.Urls.Single());
        request.Headers.Add("Origin", origin);
        request.Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"eth_chainId","params":[]}""", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.SendAsync(request);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.GetValues("Access-Control-Allow-Origin"), Is.EqualTo(new[] { origin }));
            Assert.That(response.Headers.Contains("Access-Control-Allow-Credentials"), Is.False);
            Assert.That(json.RootElement.GetProperty("result").GetString(), Is.EqualTo("0x1"));
            Assert.That(json.RootElement.GetProperty("id").GetInt32(), Is.EqualTo(1));
        }
    }

    [TestCase(ChromeOrigin, true)]
    [TestCase(FirefoxOrigin, true)]
    [TestCase("https://wallet.example", false)]
    [TestCase("null", false)]
    public async Task Websocket_upgrade_enforces_the_wallet_origin_policy(string origin, bool allowed)
    {
        await using WebApplication app = CreateApp("http://127.0.0.1:0");
        LightClientHost.ConfigureRequestPolicy(app, "http://127.0.0.1:0");
        app.UseWebSockets();
        app.MapGet("/ws", async context =>
        {
            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", context.RequestAborted);
        });
        await app.StartAsync();
        using ClientWebSocket client = new();
        client.Options.SetRequestHeader("Origin", origin);
        Uri endpoint = new(app.Urls.Single().Replace("http://", "ws://", StringComparison.Ordinal) + "/ws");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        if (allowed)
        {
            await client.ConnectAsync(endpoint, timeout.Token);
            WebSocketReceiveResult result = await client.ReceiveAsync(new ArraySegment<byte>(new byte[1]), timeout.Token);
            Assert.That(result.CloseStatus, Is.EqualTo(WebSocketCloseStatus.NormalClosure));
            await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", timeout.Token);
        }
        else
        {
            await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectAsync(endpoint, timeout.Token));
            Assert.That(client.State, Is.EqualTo(WebSocketState.Closed));
        }
    }

    [Test]
    public async Task Failed_listener_start_does_not_launch_consensus_work()
    {
        await using WebApplication occupying = CreateApp("http://127.0.0.1:0");
        await occupying.StartAsync();
        await using WebApplication app = CreateApp(occupying.Urls.Single());
        bool launched = false;

        await Assert.CatchAsync<IOException>(async () => await LightClientHost.RunAsync(app, _ =>
        {
            launched = true;
            return Task.CompletedTask;
        }));

        Assert.That(launched, Is.False);
    }

    [Test]
    public async Task Failed_initialization_stops_the_listener([Values] bool synchronous)
    {
        await using WebApplication app = CreateApp("http://127.0.0.1:0");

        await Assert.ThrowsAsync<InvalidDataException>(() => LightClientHost.RunAsync(app, _ => synchronous
            ? throw new InvalidDataException("bootstrap failed")
            : Task.FromException(new InvalidDataException("bootstrap failed"))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(app.Lifetime.ApplicationStopping.IsCancellationRequested, Is.True);
            Assert.That(app.Lifetime.ApplicationStopped.IsCancellationRequested, Is.True);
        }
    }

    [Test]
    public async Task Initialization_timeout_is_not_reclassified_as_normal_application_shutdown()
    {
        await using WebApplication app = CreateApp("http://127.0.0.1:0");

        InvalidOperationException? failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LightClientHost.RunAsync(app, _ => Task.FromCanceled(new CancellationToken(canceled: true))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.InnerException, Is.InstanceOf<OperationCanceledException>());
            Assert.That(app.Lifetime.ApplicationStopping.IsCancellationRequested, Is.True);
            Assert.That(app.Lifetime.ApplicationStopped.IsCancellationRequested, Is.True);
        }
    }

    [Test]
    public async Task Listener_serves_startup_rpc_and_progress_during_blocked_initialization()
    {
        await using WebApplication app = CreateApp("http://127.0.0.1:0");
        LightClientStatus status = new(11155111, [], _ => false, _ => false);
        LightClientHost.ConfigureRequestPolicy(app, "http://127.0.0.1:0");
        app.UseWebSockets();
        app.MapGet("/status", status.Read);
        app.MapPost("/", (HttpContext context) => RpcEndpoint.HandleAsync(context, status.InvokeAsync, NullLogger.Instance));
        app.MapGet("/ws", status.HandleWebSocketAsync);
        TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = LightClientHost.RunAsync(app, async token =>
        {
            status.SetStage(StartupStage.Bootstrap);
            initialized.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        try
        {
            await initialized.Task;
            using HttpClient client = new();
            using JsonDocument progress = JsonDocument.Parse(await client.GetStringAsync(app.Urls.Single() + "/status"));
            using StringContent request = new("""{"jsonrpc":"2.0","id":1,"method":"eth_syncing","params":[]}""", Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync(app.Urls.Single(), request);
            using JsonDocument syncing = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            using HttpRequestMessage upgrade = new(HttpMethod.Get, app.Urls.Single() + "/ws");
            upgrade.Headers.Add("Connection", "Upgrade");
            upgrade.Headers.Add("Upgrade", "websocket");
            upgrade.Headers.Add("Sec-WebSocket-Version", "13");
            upgrade.Headers.Add("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
            using HttpResponseMessage unavailable = await client.SendAsync(upgrade);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(run.IsCompleted, Is.False);
                Assert.That(progress.RootElement.GetProperty("startupStage").GetString(), Is.EqualTo("bootstrap"));
                Assert.That(progress.RootElement.GetProperty("latest").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(syncing.RootElement.GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Object));
                Assert.That(syncing.RootElement.GetProperty("result").EnumerateObject().Select(item => item.Name),
                    Is.EquivalentTo(new[] { "startingBlock", "currentBlock", "highestBlock" }));
                Assert.That(unavailable.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            }
        }
        finally
        {
            app.Lifetime.StopApplication();
            await Assert.ThrowsAsync<TaskCanceledException>(async () => await run);
        }
    }

    [Test]
    public async Task Application_shutdown_cancels_pending_rpc_work()
    {
        DefaultHttpContext context = new();
        using MemoryStream input = new(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"eth_getBalance","params":[]}"""));
        context.Request.Body = input;
        using MemoryStream output = new();
        context.Response.Body = output;
        using CancellationTokenSource stopping = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = RpcEndpoint.HandleAsync(context, async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return false;
        }, NullLogger.Instance, stoppingToken: stopping.Token);
        await entered.Task;

        await stopping.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(async () => await pending);
        Assert.That(output.Length, Is.Zero, "Shutdown must not manufacture a successful response.");
    }

    [Test]
    public async Task Host_waits_for_consensus_work_before_disposal([Values] bool failSync)
    {
        await using WebApplication app = CreateApp("http://127.0.0.1:0");
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = LightClientHost.RunAsync(app, async token =>
        {
            using CancellationTokenRegistration registration = token.Register(() => canceled.SetResult());
            ready.SetResult();
            await release.Task;
            if (failSync) throw new InvalidDataException("sync failure");
        });
        await ready.Task;
        app.Lifetime.StopApplication();
        await canceled.Task;
        Assert.That(run.IsCompleted, Is.False);
        release.SetResult();
        if (failSync)
            Assert.That(async () => await run, Throws.TypeOf<InvalidDataException>().With.Message.EqualTo("sync failure"));
        else
            await run;
        Assert.That(() => app.Services.GetService(typeof(ILoggerFactory)), Throws.Nothing);
    }

    private static WebApplication CreateApp(string url)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddCors();
        builder.WebHost.UseUrls(url);
        return builder.Build();
    }

    private static async Task SendInterruptAsync(int clientId, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new(OperatingSystem.IsWindows() ? "powershell.exe" : "kill")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($$"""
                $ErrorActionPreference = 'Stop'
                Add-Type -Name ConsoleSignal -Namespace LightClientTests -MemberDefinition @'
                [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool FreeConsole();
                [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool AttachConsole(uint pid);
                [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool SetConsoleCtrlHandler(System.IntPtr handler, bool add);
                [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern uint GetConsoleProcessList(uint[] ids, uint length);
                [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
                '@
                [LightClientTests.ConsoleSignal]::FreeConsole() | Out-Null
                if (![LightClientTests.ConsoleSignal]::AttachConsole({{clientId}})) { throw 'Cannot attach to the test child console.' }
                try {
                    $ids = New-Object uint32[] 8
                    $count = [LightClientTests.ConsoleSignal]::GetConsoleProcessList($ids, 8)
                    $senderId = [Diagnostics.Process]::GetCurrentProcess().Id
                    if ($count -ne 2 -or $ids -notcontains {{clientId}} -or $ids -notcontains $senderId) { throw 'The child console is not isolated.' }
                    if (![LightClientTests.ConsoleSignal]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)) { throw 'Cannot protect the signal sender.' }
                    if (![LightClientTests.ConsoleSignal]::GenerateConsoleCtrlEvent(0, 0)) { throw 'Cannot send Ctrl+C.' }
                    Start-Sleep -Milliseconds 100
                } finally { [LightClientTests.ConsoleSignal]::FreeConsole() | Out-Null }
                """);
        }
        else
        {
            start.ArgumentList.Add("-INT");
            start.ArgumentList.Add(clientId.ToString());
        }
        using Process sender = Process.Start(start)!;
        try
        {
            await sender.WaitForExitAsync(cancellationToken);
            Assert.That(sender.ExitCode, Is.Zero, "The interrupt must reach only the owned child process.");
        }
        finally
        {
            if (!sender.HasExited) sender.Kill(entireProcessTree: true);
            await sender.WaitForExitAsync();
        }
    }
}

public class LightClientStatusTests
{
    private static readonly Address HostedAddress = new("0x1000000000000000000000000000000000000001");
    private static readonly VerifiedHead Head = new(400, 200, Keccak.Compute("head"), Keccak.Compute("state"));

    [Test]
    public void Startup_reports_unknown_heads_and_individual_waiting_accounts(
        [Values("checkpoint", "beacon", "bootstrap", "execution")] string stageName)
    {
        StartupStage stage = Enum.Parse<StartupStage>(stageName, ignoreCase: true);
        LightClientStatus status = new(11155111, [HostedAddress], _ => false, _ => false);
        status.SetStage(stage);

        ClientProgress progress = status.Read();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.StartupStage, Is.EqualTo(stage.ToString().ToLowerInvariant()));
            Assert.That(progress.RpcInitialized, Is.False);
            Assert.That(progress.Latest, Is.Null);
            Assert.That(progress.Finalized, Is.Null);
            Assert.That(progress.History, Is.Null);
            Assert.That(progress.HostedAccounts, Has.Length.EqualTo(1));
            Assert.That(progress.HostedAccounts[0], Is.EqualTo(new HostedAccountProgress(HostedAddress.ToString(),
                null, null, null, 0, 0, false, "waiting", null, null)));
        }
    }

    [TestCase("eth_chainId", "[]", "\"0xaa36a7\"")]
    [TestCase("eth_syncing", "[]", "{\"startingBlock\":\"0x0\",\"currentBlock\":\"0x0\",\"highestBlock\":\"0x0\"}")]
    public async Task Startup_metadata_preserves_standard_rpc_shapes(string method, string parameters, string expected)
    {
        LightClientStatus status = new(11155111, [], _ => false, _ => false);
        using JsonDocument input = JsonDocument.Parse(parameters);

        object result = await status.InvokeAsync(method, input.RootElement, CancellationToken.None);

        Assert.That(JsonSerializer.Serialize(result), Is.EqualTo(expected));
    }

    [Test]
    public void Startup_metadata_rejects_invalid_parameters(
        [Values("eth_chainId", "eth_syncing")] string method, [Values("null", "{}", "[1]")] string parameters)
    {
        LightClientStatus status = new(11155111, [], _ => false, _ => false);
        using JsonDocument input = JsonDocument.Parse(parameters);

        Assert.That(async () => await status.InvokeAsync(method, input.RootElement, CancellationToken.None),
            Throws.TypeOf<RpcException>().With.Property(nameof(RpcException.Code)).EqualTo(-32602));
    }

    [Test]
    public void Startup_data_requests_return_an_actionable_error(
        [Values("eth_blockNumber", "eth_getBalance", "eth_getLogs", "eth_sendRawTransaction")] string method)
    {
        LightClientStatus status = new(11155111, [], _ => false, _ => false);
        status.SetStage(StartupStage.Bootstrap);

        Assert.That(async () => await status.InvokeAsync(method, default, CancellationToken.None),
            Throws.TypeOf<RpcException>().With.Property(nameof(RpcException.Code)).EqualTo(-32000)
                .And.Message.Contains("bootstrap").And.Message.Contains("GET /status"));
    }

    [Test]
    public async Task Running_status_keeps_freshness_cache_and_history_coverage_separate()
    {
        ulong slot = Head.Slot;
        LightClientStatus status = new(11155111, [HostedAddress],
            head => head?.IsFresh(slot, 10) ?? false, head => head?.IsFresh(slot, 300) ?? false);
        HostedAccountProgress account = new(HostedAddress.ToString(), Head.Number - 1, Keccak.Compute("previous").ToString(),
            Head.StateRoot.ToString(), 3, 4, true, "complete", null, null);
        HistoryProgress history = new(100, 200, 50, 200, true, null);
        bool invoked = false;
        status.Initialize((_, _, _) => { invoked = true; return Task.FromResult<object>("verified"); },
            _ => Task.CompletedTask, () => Head, () => Head, target =>
            {
                Assert.That(target, Is.EqualTo(Head));
                return [account];
            }, () => history);

        ClientProgress current = status.Read();
        slot += 11;
        ClientProgress staleLatest = status.Read();
        slot += 290;
        ClientProgress staleFinalized = status.Read();
        object result = await status.InvokeAsync("eth_blockNumber", default, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(current.StartupStage, Is.EqualTo("running"));
            Assert.That(current.RpcInitialized, Is.True);
            Assert.That(current.Latest!.BlockNumber, Is.EqualTo(Head.Number));
            Assert.That(current.Latest.Fresh, Is.True);
            Assert.That(staleLatest.Latest!.Fresh, Is.False);
            Assert.That(staleLatest.Finalized!.Fresh, Is.True);
            Assert.That(staleFinalized.Finalized!.Fresh, Is.False);
            Assert.That(staleLatest.HostedAccounts, Is.EqualTo(new[] { account }));
            Assert.That(staleLatest.History, Is.SameAs(history));
            Assert.That(invoked, Is.True);
            Assert.That(result, Is.EqualTo("verified"));
        }
    }

    [Test]
    public void Stopped_or_failed_runtime_rejects_data_without_touching_disposed_services(
        [Values("stopping", "failed")] string stageName)
    {
        StartupStage stage = Enum.Parse<StartupStage>(stageName, ignoreCase: true);
        LightClientStatus status = new(1, [], _ => false, _ => false);
        status.Initialize((_, _, _) => throw new AssertionException("Disposed RPC must not be called."),
            _ => throw new AssertionException("Disposed subscriptions must not be called."),
            () => null, () => null, _ => [], () => new(null, null, null, null, false, null));
        status.SetStage(stage);

        Assert.That(async () => await status.InvokeAsync("eth_getBalance", default, CancellationToken.None),
            Throws.TypeOf<RpcException>().With.Property(nameof(RpcException.Code)).EqualTo(-32000));
    }

    [Test]
    public void Startup_rpc_observes_request_cancellation()
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        using CancellationTokenSource stop = new();
        stop.Cancel();

        Assert.That(async () => await status.InvokeAsync("eth_syncing", default, stop.Token),
            Throws.TypeOf<OperationCanceledException>());
    }
}
