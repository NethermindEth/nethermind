// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Nethermind.LightClient.Test;

public class HostTests
{
    [Test]
    [NonParallelizable]
    public async Task Ctrl_C_cancels_checkpoint_fetch_before_the_rpc_host_starts()
    {
        string directory = Path.Combine(OperatingSystem.IsWindows() ? "C:/Temp" : Path.GetTempPath(),
            "NethermindLightClientShutdownTests", Guid.NewGuid().ToString("N"));
        using TcpListener provider = new(IPAddress.Loopback, 0);
        provider.Start();
        int port = ((IPEndPoint)provider.LocalEndpoint).Port;
        string assembly = typeof(LightClientHost).Assembly.Location;
        string application = Path.GetDirectoryName(assembly)!;
        ProcessStartInfo start = new("dotnet") { UseShellExecute = OperatingSystem.IsWindows() };
        if (OperatingSystem.IsWindows()) start.WindowStyle = ProcessWindowStyle.Hidden;
        foreach (string argument in new[]
        {
            "exec", "--runtimeconfig", Path.Combine(application, "Nethermind.LightClient.runtimeconfig.json"),
            "--depsfile", Path.Combine(application, "Nethermind.LightClient.deps.json"), assembly,
            "--checkpoint-url", $"https://127.0.0.1:{port}", "--urls", "http://127.0.0.1:0", "--data-dir", directory,
        }) start.ArgumentList.Add(argument);
        using Process client = Process.Start(start)!;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            using TcpClient connection = await provider.AcceptTcpClientAsync(timeout.Token);
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
    public async Task Rpc_origin_and_host_policy_rejects_cross_site_and_rebinding_requests(
        string? requestOrigin, string? requestHost, int expectedStatus)
    {
        await using WebApplication app = CreateApp("http://127.0.0.1:0");
        LightClientHost.ConfigureRequestPolicy(app, "http://127.0.0.1:0");
        app.MapGet("/", () => "ok");
        await app.StartAsync();
        using HttpClient client = new();
        using HttpRequestMessage request = new(HttpMethod.Get, app.Urls.Single());
        if (requestOrigin is not null) request.Headers.Add("Origin", requestOrigin == "local" ? app.Urls.Single() : requestOrigin);
        if (requestHost is not null) request.Headers.Host = requestHost;
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.That((int)response.StatusCode, Is.EqualTo(expectedStatus));
    }

    [Test]
    public async Task Failed_listener_start_does_not_launch_consensus_work()
    {
        await using WebApplication occupying = CreateApp("http://127.0.0.1:0");
        await occupying.StartAsync();
        await using WebApplication app = CreateApp(occupying.Urls.Single());
        bool launched = false;

        Assert.CatchAsync<IOException>(async () => await LightClientHost.RunAsync(app, _ =>
        {
            launched = true;
            return Task.CompletedTask;
        }));

        Assert.That(launched, Is.False);
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
