// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.LightClient.Consensus;

namespace Nethermind.LightClient;

/// <summary>Serves bounded P2P records for independent verification by browser clients.</summary>
internal static class LightClientRelay
{
    internal static void Map(WebApplication app, BeaconPeerTransport beacon, ExecutionPeerTransport execution,
        VerifiedRpc rpc, BeaconChainSpec spec, ILogger logger)
    {
        SemaphoreSlim requests = new(4, 4);
        app.Lifetime.ApplicationStopped.Register(requests.Dispose);
        app.MapGet("/lightclient/bootstrap/{root}", (HttpContext context, string root) => ServeAsync(context, async token =>
        {
            Hash256 checkpoint = ParseHash(root);
            LightClientBootstrap bootstrap = await beacon.BootstrapAsync(checkpoint, (value, validationToken) =>
            {
                _ = new LightClientStore(spec, checkpoint, value, spec.GetSlotAtTime((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                return Task.CompletedTask;
            }, token);
            return LightClientWireCodec.EncodeBootstrap(bootstrap, spec);
        }));
        app.MapGet("/lightclient/finality", (HttpContext context) => ServeAsync(context, async token =>
        {
            LightClientFinalityUpdate update = await beacon.PollFinalityAsync(static (_, _) => Task.CompletedTask, token)
                ?? throw new IOException("No finality update is available.");
            return LightClientWireCodec.EncodeFinality(update, spec);
        }));
        app.MapGet("/lightclient/optimistic", (HttpContext context) => ServeAsync(context, async token =>
        {
            LightClientOptimisticUpdate update = await beacon.PollOptimisticAsync(static (_, _) => Task.CompletedTask, token)
                ?? throw new IOException("No optimistic update is available.");
            return LightClientWireCodec.EncodeOptimistic(update, spec);
        }));
        app.MapGet("/lightclient/header/{hash}", (HttpContext context, string hash) =>
            ServeAsync(context, token => rpc.GetAuthenticatedHeaderRlpAsync(ParseHash(hash), token)));
        app.MapGet("/lightclient/code/{hash}", (HttpContext context, string hash) => ServeAsync(context, token =>
            execution.GetCodeAsync(new Account(0, 0, Keccak.EmptyTreeHash, ParseHash(hash)), token)));
        app.MapGet("/lightclient/updates", (HttpContext context) => ServeAsync(context, async token =>
        {
            if (!ulong.TryParse(context.Request.Query["start_period"], out ulong start) ||
                !int.TryParse(context.Request.Query["count"], out int count) || count is < 1 or > 8 ||
                start > ulong.MaxValue - (ulong)count)
                throw new ArgumentException("Updates require start_period and a count between 1 and 8.");
            ulong currentPeriod = spec.GetEpoch(spec.GetSlotAtTime((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds())) / Presets.EpochsPerSyncCommitteePeriod;
            count = start > currentPeriod ? 0 : (int)Math.Min((ulong)count, currentPeriod - start + 1);
            string[] updates = new string[count];
            for (int i = 0; i < count; i++)
            {
                LightClientUpdate update = await beacon.UpdateAsync(start + (ulong)i, static (_, _) => Task.CompletedTask, token);
                updates[i] = Convert.ToBase64String(LightClientWireCodec.EncodeUpdate(update, spec));
            }
            context.Response.ContentType = "application/json";
            return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(updates);
        }));

        async Task ServeAsync(HttpContext context, Func<CancellationToken, Task<byte[]>> response)
        {
            if (!requests.Wait(0))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }
            try
            {
                using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
                using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(
                    context.RequestAborted, app.Lifetime.ApplicationStopping, deadline.Token);
                byte[] bytes = await response(stop.Token);
                context.Response.ContentType ??= "application/octet-stream";
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.Body.WriteAsync(bytes, stop.Token);
            }
            catch (Exception exception) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
            {
                context.Response.StatusCode = exception is ArgumentException or FormatException
                    ? StatusCodes.Status400BadRequest : StatusCodes.Status503ServiceUnavailable;
                logger.LogDebug(exception, "Light-client relay request failed");
            }
            finally { requests.Release(); }
        }
    }

    private static Hash256 ParseHash(string value) => value.Length == 66 && value.StartsWith("0x", StringComparison.Ordinal)
        ? new Hash256(value) : throw new ArgumentException("A 32-byte hex hash is required.");
}
