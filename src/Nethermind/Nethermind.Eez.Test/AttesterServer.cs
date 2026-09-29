// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Prove;
using Nethermind.Logging;

namespace Nethermind.Eez.Test;

/// <summary>The attester, in process behind a test server, for a recorded window's chain and the devnet attester's key.</summary>
internal sealed class AttesterServer : IAsyncDisposable
{
    public static readonly PrivateKey AttesterKey = new("59c6995e998f97a5a0044966f0945389dc9e86dae88c7a8412f4603b6b78690d");

    private readonly WebApplication _app;
    private readonly GrpcChannel _channel;

    private AttesterServer(WebApplication app, GrpcChannel channel)
    {
        _app = app;
        _channel = channel;
        Client = new Prover.ProverClient(channel);
    }

    public Prover.ProverClient Client { get; }

    public GrpcChannel Channel => _channel;

    public static async Task<AttesterServer> Start(string fixture, TimeSpan? idleTimeout = null)
    {
        JsonElement oracle = StatelessFixtures.ReadJson(fixture, "oracle.json");
        ISpecProvider specProvider = StatelessFixtures.ReadSpecProvider(fixture, "chain-config.json");
        ulong blockTime = oracle.TryGetProperty("l2_block_time_seconds", out JsonElement time) ? time.GetUInt64() : 2;
        EezSettlementContext context = new(oracle.GetProperty("rollup_id").GetUInt64(), specProvider.ChainId, new Address(oracle.GetProperty("proof_system").GetString()!),
            new ValueHash256(oracle.GetProperty("proof_system_vkey").GetString()!), blockTime);
        AttesterOptions options = new(IPEndPoint.Parse("127.0.0.1:0"), "", context.RollupId, context.VerificationKey, AttesterKey.Address, context.ProofSystem, "", "", blockTime,
            context.GasLimit, WindowLimits.Default, idleTimeout ?? TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        WebApplication app = Program.Build(builder, options, new AttestationPipeline(specProvider, context, new EezAttestationSigner(AttesterKey), LimboLogs.Instance));
        await app.StartAsync();
        GrpcChannel channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = app.GetTestServer().CreateHandler() });
        return new AttesterServer(app, channel);
    }

    public async Task<ProveResponse> Prove(IEnumerable<ProveChunk> chunks)
    {
        using AsyncClientStreamingCall<ProveChunk, ProveResponse> call = Client.Prove();
        try
        {
            foreach (ProveChunk chunk in chunks)
            {
                await call.RequestStream.WriteAsync(chunk);
            }

            await call.RequestStream.CompleteAsync();
        }
        catch (RpcException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return await call.ResponseAsync;
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
