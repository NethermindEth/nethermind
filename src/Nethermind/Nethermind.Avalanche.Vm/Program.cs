// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Health.V1;
using Grpc.HealthCheck;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nethermind.Avalanche.Vm;

// Generated from proto/vm/runtime/runtime.proto (package "vm.runtime" => C# namespace "Vm.Runtime").
using RuntimePb = global::Vm.Runtime;

// AvalancheGo rpcchainvm protocol version targeted by this VM (avalanchego v1.14.2, version/constants.go).
const uint ProtocolVersion = 45;

// h2c (HTTP/2 without TLS) for both the server and the outbound gRPC clients.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

// --- 1. Reverse handshake input: the address of AvalancheGo's Runtime server. ---
string? engineAddr = Environment.GetEnvironmentVariable("AVALANCHE_VM_RUNTIME_ENGINE_ADDR");
if (string.IsNullOrEmpty(engineAddr))
{
    await Console.Error.WriteLineAsync("AVALANCHE_VM_RUNTIME_ENGINE_ADDR is not set; this binary must be launched by AvalancheGo.");
    return 1;
}

// --- 2. Build the Kestrel HTTP/2 (h2c, no TLS) gRPC server on an ephemeral loopback port. ---
// Bind via UseUrls(":0") rather than an explicit Listen() call so that Kestrel populates
// IServerAddressesFeature with the OS-assigned port after start (an explicit Listen would leave it empty).
// ConfigureEndpointDefaults forces HTTP/2 (h2c) on that URL-bound endpoint.
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.WebHost.ConfigureKestrel(static options =>
{
    options.Limits.MaxRequestBodySize = null;
    options.ConfigureEndpointDefaults(static listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Services.AddGrpc(static grpc =>
{
    // rpcchainvm exchanges whole blocks; do not cap message sizes.
    grpc.MaxReceiveMessageSize = null;
    grpc.MaxSendMessageSize = null;
});

AvalancheVmService vmService = new();
HealthServiceImpl healthService = new();
builder.Services.AddSingleton(vmService);
builder.Services.AddSingleton(healthService);

WebApplication app = builder.Build();

app.MapGrpcService<AvalancheVmService>();
app.MapGrpcService<HealthServiceImpl>();

// Mark the VM service (and the overall server) as serving.
healthService.SetStatus(string.Empty, HealthCheckResponse.Types.ServingStatus.Serving);
healthService.SetStatus("vm.VM", HealthCheckResponse.Types.ServingStatus.Serving);

await app.StartAsync();

// --- 3. Resolve the actual bound port and announce ourselves to the engine within 5s. ---
string serverAddress = ResolveLoopbackAddress(app);

using (GrpcChannel runtimeChannel = CreateInsecureChannel(engineAddr))
{
    RuntimePb.Runtime.RuntimeClient runtimeClient = new(runtimeChannel);
    using CancellationTokenSource initTimeout = new(TimeSpan.FromSeconds(5));
    await runtimeClient.InitializeAsync(
        new RuntimePb.InitializeRequest { ProtocolVersion = ProtocolVersion, Addr = serverAddress },
        cancellationToken: initTimeout.Token);
}

// --- 4. Serve until the Shutdown RPC arrives. Per the rpcchainvm contract, OS termination signals are
//        ignored: AvalancheGo drives lifecycle through the Shutdown RPC, after which we stop gracefully. ---
Console.CancelKeyPress += static (_, eventArgs) => eventArgs.Cancel = true; // swallow SIGINT (Ctrl+C)
using PosixSignalRegistration sigTerm = PosixSignalRegistration.Create(
    PosixSignal.SIGTERM, static ctx => ctx.Cancel = true); // swallow SIGTERM

try
{
    await Task.Delay(Timeout.Infinite, vmService.ShutdownRequested);
}
catch (OperationCanceledException)
{
    // Shutdown RPC received.
}

await app.StopAsync();
return 0;

// Reads the loopback host:port Kestrel actually bound to (port 0 => OS-assigned).
static string ResolveLoopbackAddress(WebApplication app)
{
    IServerAddressesFeature? addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
    string? bound = null;
    if (addresses is not null)
    {
        foreach (string address in addresses.Addresses)
        {
            bound = address;
            break;
        }
    }

    if (bound is null)
    {
        throw new InvalidOperationException("Kestrel did not report a bound address.");
    }

    // bound looks like "http://127.0.0.1:49152"; AvalancheGo wants a bare host:port.
    Uri uri = new(bound);
    return $"127.0.0.1:{uri.Port}";
}

// Opens an insecure h2c channel to a gRPC server addressed as host:port (or a full http URL).
static GrpcChannel CreateInsecureChannel(string address)
{
    string target = address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        ? address
        : "http://" + address;

    return GrpcChannel.ForAddress(
        target,
        new GrpcChannelOptions
        {
            Credentials = ChannelCredentials.Insecure,
            MaxReceiveMessageSize = null,
            MaxSendMessageSize = null,
        });
}
