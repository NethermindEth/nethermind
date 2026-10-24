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
using NSubstitute;

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
    [CancelAfter(10_000)]
    [NonParallelizable]
    public async Task Startup_rpc_wait_observes_http_initialization_and_cancellation_budget(
        [Values("initialize", "shutdown", "deadline", "mixed-cancel")] string outcome, CancellationToken token)
    {
        const string parameters = "[\"0x1000000000000000000000000000000000000001\",\"latest\"]";
        string readRequest = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"eth_getBalance\",\"params\":" + parameters + "}";
        string request = outcome == "mixed-cancel"
            ? "[" + readRequest + ",{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"eth_sendRawTransaction\",\"params\":[\"0xc0\"]}]"
            : readRequest;
        LightClientStatus status = new(11155111, [], _ => false, _ => false);
        using MemoryStream input = new(Encoding.UTF8.GetBytes(request));
        using MemoryStream output = new();
        DefaultHttpContext context = new();
        context.Request.Body = input;
        context.Response.Body = output;
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (outcome == "mixed-cancel") context.RequestAborted = stopping.Token;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TimerCallback expire = _ => throw new AssertionException("The endpoint did not create its request deadline.");
        object? timerState = null;
        TimeSpan dueTime = default;
        TimeProvider clock = Substitute.For<TimeProvider>();
        clock.CreateTimer(Arg.Any<TimerCallback>(), Arg.Any<object?>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>()).Returns(call =>
        {
            expire = call.ArgAt<TimerCallback>(0);
            timerState = call.ArgAt<object?>(1);
            dueTime = call.ArgAt<TimeSpan>(2);
            return Substitute.For<ITimer>();
        });
        List<(string Method, string Parameters)> invoked = [];
        Task pending = RpcEndpoint.HandleAsync(context, (method, values, caller) =>
        {
            try { return status.InvokeAsync(method, values, caller); }
            finally { entered.TrySetResult(); }
        }, NullLogger.Instance, clock, outcome == "mixed-cancel" ? token : stopping.Token);
        await entered.Task.WaitAsync(token);
        bool completedBeforeInitialization = pending.IsCompleted;

        if (outcome == "initialize") Initialize();
        else if (outcome is "shutdown" or "mixed-cancel") await stopping.CancelAsync();
        else expire(timerState);

        if (outcome is "shutdown" or "mixed-cancel")
            Assert.That(async () => await pending.WaitAsync(token), Throws.InstanceOf<OperationCanceledException>());
        else
            await pending.WaitAsync(token);
        int completedCalls = invoked.Count;
        if (outcome != "initialize") Initialize();

        using MemoryStream nextInput = new(Encoding.UTF8.GetBytes(readRequest));
        using MemoryStream nextOutput = new();
        DefaultHttpContext next = new();
        next.Request.Body = nextInput;
        next.Response.Body = nextOutput;
        await RpcEndpoint.HandleAsync(next, status.InvokeAsync, NullLogger.Instance, stoppingToken: token);
        using JsonDocument nextResponse = JsonDocument.Parse(nextOutput.ToArray());
        string nextResult = StringRpcResult(nextResponse);
        if (outcome == "initialize")
        {
            using JsonDocument response = JsonDocument.Parse(output.ToArray());
            Assert.That(StringRpcResult(response), Is.EqualTo("0x2a"));
        }
        else Assert.That(output.Length, Is.Zero, "a canceled startup read must not produce a successful response");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(completedBeforeInitialization, Is.False);
            Assert.That(dueTime, Is.EqualTo(TimeSpan.FromSeconds(90)));
            Assert.That(completedCalls, Is.EqualTo(outcome == "initialize" ? 1 : 0));
            if (outcome is "initialize" or "deadline")
                Assert.That(context.Response.StatusCode, Is.EqualTo(outcome == "deadline" ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status200OK));
            if (outcome == "mixed-cancel") Assert.That(context.RequestAborted.IsCancellationRequested, Is.True);
            Assert.That(next.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(nextResult, Is.EqualTo("0x2a"));
            Assert.That(invoked, Is.EqualTo(Enumerable.Repeat(("eth_getBalance", parameters), outcome == "initialize" ? 2 : 1)));
        }

        void Initialize() => status.Initialize((method, values, _) =>
            {
                invoked.Add((method, values.GetRawText()));
                return Task.FromResult<object>("0x2a");
            }, static _ => Task.CompletedTask, static () => null, static () => null,
            static _ => [], static () => new(null, null, null, null, false, null));
    }

    private static string StringRpcResult(JsonDocument response)
    {
        Assert.That(response.RootElement.TryGetProperty("result", out JsonElement result), Is.True, response.RootElement.GetRawText());
        Assert.That(result.ValueKind, Is.EqualTo(JsonValueKind.String));
        return result.GetString()!;
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
    public void Startup_transaction_submission_returns_an_actionable_error()
    {
        LightClientStatus status = new(11155111, [], _ => false, _ => false);
        status.SetStage(StartupStage.Bootstrap);
        int invoked = 0;

        Assert.That(() => { _ = status.InvokeAsync("eth_sendRawTransaction", default, CancellationToken.None); },
            Throws.TypeOf<RpcException>().With.Property(nameof(RpcException.Code)).EqualTo(-32000)
                .And.Message.Contains("bootstrap").And.Message.Contains("GET /status"));
        Initialize(status, (_, _, _) => { invoked++; return Task.FromResult<object>("submitted"); });
        Assert.That(invoked, Is.Zero, "a rejected submission must not be queued for initialization");
    }

    [TestCase("eth_blockNumber", "[]")]
    [TestCase("eth_getBalance", "[\"0x1000000000000000000000000000000000000001\",\"latest\"]")]
    [TestCase("eth_getLogs", "[{\"fromBlock\":\"0x1\",\"toBlock\":\"0x2\"}]")]
    [TestCase("unknown_method", "[]")]
    [CancelAfter(10_000)]
    public async Task Startup_requests_wait_for_runtime_and_preserve_arguments(string method, string parameters, CancellationToken token)
    {
        LightClientStatus status = new(11155111, [], _ => false, _ => false);
        using JsonDocument input = JsonDocument.Parse(parameters);
        object expected = new();
        List<(string Method, string Parameters, CancellationToken Token)> invoked = [];
        Task<object> pending = status.InvokeAsync(method, input.RootElement, token);
        bool completedBeforeInitialization = pending.IsCompleted;
        object chainId = await status.InvokeAsync("eth_chainId", default, token);
        object syncing = await status.InvokeAsync("eth_syncing", default, token);
        ClientProgress progress = status.Read();

        Initialize(status, (name, values, caller) =>
        {
            invoked.Add((name, values.GetRawText(), caller));
            return Task.FromResult(expected);
        });
        object result = await pending.WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completedBeforeInitialization, Is.False);
            Assert.That(progress.RpcInitialized, Is.False);
            Assert.That(chainId, Is.EqualTo("0xaa36a7"));
            Assert.That(JsonSerializer.Serialize(syncing), Is.EqualTo("{\"startingBlock\":\"0x0\",\"currentBlock\":\"0x0\",\"highestBlock\":\"0x0\"}"));
            Assert.That(result, Is.SameAs(expected));
            Assert.That(invoked, Is.EqualTo(new[] { (method, parameters, token) }));
            Assert.That(status.Read().RpcInitialized, Is.True);
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Intermediate_startup_stages_do_not_release_pending_requests(CancellationToken token)
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        Task<object> pending = status.InvokeAsync("eth_blockNumber", default, token);
        List<bool> completed = [];
        foreach (StartupStage stage in new[] { StartupStage.Checkpoint, StartupStage.Beacon, StartupStage.Bootstrap, StartupStage.Execution })
        {
            status.SetStage(stage);
            completed.Add(pending.IsCompleted);
        }
        Initialize(status, static (_, _, _) => Task.FromResult<object>("verified"));
        object result = await pending.WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.EqualTo(new[] { false, false, false, false }));
            Assert.That(result, Is.EqualTo("verified"));
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Running_dispatch_returns_the_runtime_task_directly(
        [Values("eth_blockNumber", "eth_chainId", "eth_syncing", "eth_sendRawTransaction")] string method, CancellationToken token)
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        TaskCompletionSource<object> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<object> expected = completion.Task;
        Initialize(status, (_, _, _) => expected);
        Task<object> actual = status.InvokeAsync(method, default, token);
        try
        {
            using IDisposable scope = Assert.EnterMultipleScope();
            Assert.That(actual, Is.SameAs(expected));
            Assert.That(actual.IsCompleted, Is.False);
        }
        finally
        {
            completion.TrySetResult(new object());
            await ((Task)actual).WaitAsync(token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Canceling_one_startup_request_does_not_cancel_other_waiters(CancellationToken token)
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<object> canceled = status.InvokeAsync("eth_getBalance", default, caller.Token);
        Task<object> survivor = status.InvokeAsync("eth_blockNumber", default, token);
        await caller.CancelAsync();
        bool cancellationObserved;
        try { await canceled.WaitAsync(token); cancellationObserved = false; }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { cancellationObserved = true; }
        bool survivorCompleted = survivor.IsCompleted;
        List<string> invoked = [];
        Initialize(status, (method, _, _) => { invoked.Add(method); return Task.FromResult<object>("verified"); });
        object result = await survivor.WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cancellationObserved, Is.True);
            Assert.That(survivorCompleted, Is.False);
            Assert.That(result, Is.EqualTo("verified"));
            Assert.That(invoked, Is.EqualTo(new[] { "eth_blockNumber" }));
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Terminal_startup_wakes_waiters_and_rejects_late_initialization(
        [Values("stopping", "failed")] string stageName, CancellationToken token)
    {
        StartupStage terminal = Enum.Parse<StartupStage>(stageName, ignoreCase: true);
        LightClientStatus status = new(1, [], _ => false, _ => false);
        Task<object> pending = status.InvokeAsync("eth_getBalance", default, token);
        status.SetStage(terminal);
        Initialize(status, static (_, _, _) => throw new AssertionException("Terminal startup must not publish runtime services."));
        status.SetStage(StartupStage.Execution);

        Assert.That(async () => await pending.WaitAsync(token), Throws.TypeOf<RpcException>()
            .With.Property(nameof(RpcException.Code)).EqualTo(-32000).And.Message.Contains(terminal.ToString().ToLowerInvariant()));
        Assert.That(async () => await status.InvokeAsync("eth_blockNumber", default, token), Throws.TypeOf<RpcException>()
            .With.Property(nameof(RpcException.Code)).EqualTo(-32000));
        ClientProgress progress = status.Read();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.StartupStage, Is.EqualTo(terminal.ToString().ToLowerInvariant()));
            Assert.That(progress.RpcInitialized, Is.False);
        }
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
    public void Startup_rpc_observes_request_cancellation(
        [Values("eth_chainId", "eth_syncing", "eth_getBalance", "eth_sendRawTransaction")] string method)
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        using CancellationTokenSource stop = new();
        stop.Cancel();

        Assert.That(async () => await status.InvokeAsync(method, default, stop.Token),
            Throws.TypeOf<OperationCanceledException>());
    }

    private static void Initialize(LightClientStatus status, Func<string, JsonElement, CancellationToken, Task<object>> invoke) =>
        status.Initialize(invoke, static _ => Task.CompletedTask, static () => null, static () => null,
            static _ => [], static () => new(null, null, null, null, false, null));
}
