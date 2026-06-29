// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

// Generated from proto/vm/vm.proto (package "vm" => C# namespace "Vm").
using VmPb = global::Vm;

namespace Nethermind.Avalanche.Vm.Test;

/// <summary>
/// End-to-end proof that the <c>Nethermind.Avalanche.Vm</c> executable completes AvalancheGo's
/// reverse-gRPC handshake and then serves the <c>vm.VM</c> service, without needing a real AvalancheGo.
/// </summary>
/// <remarks>
/// The test plays the AvalancheGo "Runtime" engine: it hosts a Kestrel h2c gRPC server implementing
/// <c>vm.runtime.Runtime</c>, points the VM at it via <c>AVALANCHE_VM_RUNTIME_ENGINE_ADDR</c>, launches
/// the VM as a <c>dotnet &lt;dll&gt;</c> subprocess, waits for the VM to call back into
/// <c>Runtime.Initialize</c>, then dials the VM's reported address and exercises a few stateless RPCs
/// before driving the VM down through its <c>Shutdown</c> RPC.
/// </remarks>
[TestFixture]
public sealed class AvalancheVmHandshakeTests
{
    // AvalancheGo v1.14.2 rpcchainvm protocol version (version/constants.go). The VM reports this on Initialize.
    private const uint ExpectedProtocolVersion = 45;

    private const string RuntimeEngineAddrEnvVar = "AVALANCHE_VM_RUNTIME_ENGINE_ADDR";

    [Test]
    public async Task Vm_completes_reverse_handshake_and_serves_vm_service()
    {
        string? vmDllPath = TryResolveVmDllPath();
        if (vmDllPath is null)
        {
            Assert.Ignore(
                "Could not locate the built Nethermind.Avalanche.Vm.dll next to the test artifacts. " +
                "Build the Nethermind.Avalanche.Vm project so this end-to-end handshake test can launch it.");
        }

        // h2c (HTTP/2 without TLS) for the outbound gRPC client we use to dial the VM.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        await using FakeRuntimeEngine engine = await FakeRuntimeEngine.StartAsync();

        using CancellationTokenSource testTimeout = new(TimeSpan.FromSeconds(60));

        Process vmProcess = StartVmSubprocess(vmDllPath!, engine.Address);
        try
        {
            // (c) The VM must call back into Runtime.Initialize with protocol 45 and its own loopback address.
            RuntimeInitialize handshake = await AwaitWithProcessGuard(
                engine.Initialized, vmProcess, TimeSpan.FromSeconds(15), "Runtime.Initialize handshake callback");

            Assert.That(handshake.ProtocolVersion, Is.EqualTo(ExpectedProtocolVersion),
                "VM reported an unexpected rpcchainvm protocol version.");
            Assert.That(handshake.Addr, Is.Not.Null.And.Not.Empty, "VM reported an empty server address.");
            Assert.That(handshake.Addr, Does.StartWith("127.0.0.1:"),
                "VM should serve vm.VM on an ephemeral loopback port.");
            Assert.That(TryParsePort(handshake.Addr), Is.GreaterThan(0),
                "VM reported a non-numeric or zero port.");

            // (d) Dial the VM's vm.VM service and exercise a few stateless RPCs.
            using GrpcChannel vmChannel = CreateInsecureChannel(handshake.Addr);
            VmPb.VM.VMClient vmClient = new(vmChannel);

            VmPb.VersionResponse version =
                await vmClient.VersionAsync(new Empty(), cancellationToken: testTimeout.Token);
            Assert.That(version.Version, Is.Not.Null.And.Not.Empty, "VM returned an empty Version.");

            Assert.DoesNotThrowAsync(
                async () => await vmClient.HealthAsync(new Empty(), cancellationToken: testTimeout.Token),
                "VM.Health should not fault.");

            VmPb.StateSyncEnabledResponse stateSync =
                await vmClient.StateSyncEnabledAsync(new Empty(), cancellationToken: testTimeout.Token);
            Assert.That(stateSync.Enabled, Is.False, "VM should report state sync disabled.");

            // (e) Drive the VM down via its Shutdown RPC and confirm the process exits.
            await vmClient.ShutdownAsync(new Empty(), cancellationToken: testTimeout.Token);

            bool exited = await WaitForExitAsync(vmProcess, TimeSpan.FromSeconds(15));
            Assert.That(exited, Is.True, "VM process did not exit after the Shutdown RPC.");
            Assert.That(vmProcess.ExitCode, Is.EqualTo(0), "VM process exited with a non-zero code.");
        }
        finally
        {
            KillIfRunning(vmProcess);
            vmProcess.Dispose();
        }
    }

    /// <summary>Launches the VM exe as <c>dotnet &lt;dll&gt;</c> with the runtime engine address forwarded.</summary>
    private static Process StartVmSubprocess(string vmDllPath, string engineAddress)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(vmDllPath)!,
        };
        startInfo.ArgumentList.Add(vmDllPath);
        startInfo.Environment[RuntimeEngineAddrEnvVar] = engineAddress;

        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };

        // Drain the child's stdio so a chatty VM can't deadlock on a full pipe buffer; surface it on failure.
        List<string> stderr = [];
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr)
                {
                    stderr.Add(e.Data);
                }
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the Nethermind.Avalanche.Vm subprocess.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>
    /// Awaits <paramref name="task"/> but fails fast if the VM subprocess dies first, or the timeout elapses,
    /// so a crashed VM surfaces a clear assertion instead of hanging CI for the full timeout.
    /// </summary>
    private static async Task<T> AwaitWithProcessGuard<T>(Task<T> task, Process process, TimeSpan timeout, string what)
    {
        using CancellationTokenSource timeoutCts = new(timeout);
        Task delay = Task.Delay(Timeout.Infinite, timeoutCts.Token);
        Task exited = process.WaitForExitAsync();

        Task winner = await Task.WhenAny(task, exited, delay);

        // Cancel and observe the still-pending delay so it does not surface as an unobserved exception.
        if (winner != delay)
        {
            timeoutCts.Cancel();
            await delay.ContinueWith(static _ => { }, TaskScheduler.Default);
        }

        if (winner == task)
        {
            return await task;
        }

        if (winner == exited)
        {
            Assert.Fail($"VM process exited (code {process.ExitCode}) before the {what} arrived.");
        }

        Assert.Fail($"Timed out after {timeout.TotalSeconds:N0}s waiting for the {what}.");
        throw new InvalidOperationException("unreachable");
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process was never started or already reaped.
        }
    }

    /// <summary>
    /// Resolves the path to the built <c>Nethermind.Avalanche.Vm.dll</c>. The repo uses a shared artifacts
    /// layout (<c>artifacts/bin/&lt;project&gt;/&lt;config&gt;/</c>), so the VM dll is a sibling of this test
    /// assembly's directory. Returns <c>null</c> if it cannot be found.
    /// </summary>
    private static string? TryResolveVmDllPath()
    {
        const string vmDllName = "Nethermind.Avalanche.Vm.dll";
        string baseDir = AppContext.BaseDirectory;

        // Typical: .../artifacts/bin/Nethermind.Avalanche.Vm.Test/<config>/.
        // The VM lives at .../artifacts/bin/Nethermind.Avalanche.Vm/<config>/.
        DirectoryInfo? testConfigDir = new(baseDir);
        string? config = testConfigDir.Name; // e.g. "debug" / "release"
        DirectoryInfo? binDir = testConfigDir.Parent?.Parent; // .../artifacts/bin

        List<string> candidates = [];
        if (binDir is not null && config is not null)
        {
            candidates.Add(Path.Combine(binDir.FullName, "Nethermind.Avalanche.Vm", config, vmDllName));
        }

        if (binDir is not null)
        {
            // Fall back to whichever config of the VM happens to be built.
            candidates.Add(Path.Combine(binDir.FullName, "Nethermind.Avalanche.Vm", "release", vmDllName));
            candidates.Add(Path.Combine(binDir.FullName, "Nethermind.Avalanche.Vm", "debug", vmDllName));
        }

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Last resort: search the artifacts bin tree for the VM dll under any config.
        if (binDir is not null && Directory.Exists(binDir.FullName))
        {
            string vmRoot = Path.Combine(binDir.FullName, "Nethermind.Avalanche.Vm");
            if (Directory.Exists(vmRoot))
            {
                foreach (string found in Directory.EnumerateFiles(vmRoot, vmDllName, SearchOption.AllDirectories))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static int TryParsePort(string hostPort)
    {
        int colon = hostPort.LastIndexOf(':');
        return colon >= 0 && int.TryParse(hostPort.AsSpan(colon + 1), out int port) ? port : 0;
    }

    /// <summary>Opens an insecure h2c gRPC channel to a server addressed as <c>host:port</c>.</summary>
    private static GrpcChannel CreateInsecureChannel(string hostPort) =>
        GrpcChannel.ForAddress(
            "http://" + hostPort,
            new GrpcChannelOptions
            {
                Credentials = ChannelCredentials.Insecure,
                MaxReceiveMessageSize = null,
                MaxSendMessageSize = null,
            });

    /// <summary>The handshake details captured from the VM's <c>Runtime.Initialize</c> callback.</summary>
    private readonly record struct RuntimeInitialize(uint ProtocolVersion, string Addr);

    /// <summary>
    /// An in-process Kestrel h2c gRPC server hosting the <c>vm.runtime.Runtime</c> service, standing in for
    /// AvalancheGo. Exposes the bound loopback <see cref="Address"/> and a task that completes with the
    /// handshake the VM reports.
    /// </summary>
    private sealed class FakeRuntimeEngine : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly FakeRuntimeService _service;

        private FakeRuntimeEngine(WebApplication app, FakeRuntimeService service, string address)
        {
            _app = app;
            _service = service;
            Address = address;
        }

        /// <summary>The <c>host:port</c> the engine bound to, suitable for AVALANCHE_VM_RUNTIME_ENGINE_ADDR.</summary>
        public string Address { get; }

        /// <summary>Completes with the protocol version and address the VM reports on its handshake.</summary>
        public Task<RuntimeInitialize> Initialized => MapInitialized();

        public static async Task<FakeRuntimeEngine> StartAsync()
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();

            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.WebHost.ConfigureKestrel(static options =>
            {
                options.Limits.MaxRequestBodySize = null;
                options.ConfigureEndpointDefaults(static listen => listen.Protocols = HttpProtocols.Http2);
            });

            builder.Services.AddGrpc(static grpc =>
            {
                grpc.MaxReceiveMessageSize = null;
                grpc.MaxSendMessageSize = null;
            });

            FakeRuntimeService service = new();
            builder.Services.AddSingleton(service);

            WebApplication app = builder.Build();
            app.MapGrpcService<FakeRuntimeService>();

            await app.StartAsync();

            string address = ResolveLoopbackAddress(app);
            return new FakeRuntimeEngine(app, service, address);
        }

        private async Task<RuntimeInitialize> MapInitialized()
        {
            global::Vm.Runtime.InitializeRequest request = await _service.Initialized;
            return new RuntimeInitialize(request.ProtocolVersion, request.Addr);
        }

        private static string ResolveLoopbackAddress(WebApplication app)
        {
            IServerAddressesFeature? addresses =
                app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();

            if (addresses is not null)
            {
                foreach (string address in addresses.Addresses)
                {
                    Uri uri = new(address);
                    return $"127.0.0.1:{uri.Port}";
                }
            }

            throw new InvalidOperationException("Fake Runtime engine did not report a bound address.");
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
