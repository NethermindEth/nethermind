// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary>
/// <c>/eth/v1/events</c>: server-sent events for <c>head</c> and <c>finalized_checkpoint</c>.
/// </summary>
/// <remarks>
/// The driver has no publish/subscribe hook for these transitions (adding one means editing
/// <see cref="Sync.BeaconSyncOrchestrator"/>, which is outside this change's allowed files - see the
/// report), so this polls <see cref="IBeaconChainStatusSource.CurrentStatus"/> and emits an event
/// whenever the observed root changes. Every emitted event still carries a real, current root/slot;
/// it is coarser-grained (bounded by <see cref="PollInterval"/>) than a true push feed, not fabricated.
/// </remarks>
internal static class EventsEndpoint
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly string[] SupportedTopics = ["head", "finalized_checkpoint"];

    public static void Map(WebApplication app, BeaconApiContext ctx) =>
        app.MapGet("/eth/v1/events", (HttpContext c) => Stream(c, ctx));

    private static async Task Stream(HttpContext c, BeaconApiContext ctx)
    {
        string[] topics = c.Request.Query["topics"].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (topics.Length == 0)
        {
            await ApiErrors.Write(c, StatusCodes.Status400BadRequest, "The 'topics' query parameter is required.", c.RequestAborted);
            return;
        }

        foreach (string topic in topics)
        {
            if (Array.IndexOf(SupportedTopics, topic) < 0)
            {
                await ApiErrors.Write(c, StatusCodes.Status400BadRequest,
                    $"Unsupported topic '{topic}'; this node only supports: {string.Join(", ", SupportedTopics)} (it is non-attesting, so validator-duty topics are not produced).",
                    c.RequestAborted);
                return;
            }
        }

        bool wantsHead = Array.IndexOf(topics, "head") >= 0;
        bool wantsFinalized = Array.IndexOf(topics, "finalized_checkpoint") >= 0;

        c.Response.ContentType = "text/event-stream";
        c.Response.Headers.CacheControl = "no-cache";
        await c.Response.Body.FlushAsync(c.RequestAborted);

        Hash256? lastHead = Hash256.Zero;
        ulong lastFinalizedEpoch = ulong.MaxValue;
        try
        {
            while (!c.RequestAborted.IsCancellationRequested)
            {
                StatusMessageV2 status = ctx.StatusSource.CurrentStatus;
                if (wantsHead && status.HeadRoot != Hash256.Zero && status.HeadRoot != lastHead)
                {
                    lastHead = status.HeadRoot;
                    await WriteEventAsync(c, "head",
                        new HeadEventDto(status.HeadSlot.ToString(), status.HeadRoot!.ToString(), ResponseEnvelope.ExecutionOptimistic()));
                }

                if (wantsFinalized && status.FinalizedRoot != Hash256.Zero && status.FinalizedEpoch != lastFinalizedEpoch)
                {
                    lastFinalizedEpoch = status.FinalizedEpoch;
                    await WriteEventAsync(c, "finalized_checkpoint",
                        new FinalizedEventDto(status.FinalizedEpoch.ToString(), status.FinalizedRoot!.ToString()));
                }

                await Task.Delay(PollInterval, c.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected or the process is shutting down; nothing left to write.
        }
    }

    private static async Task WriteEventAsync<T>(HttpContext c, string eventName, T data)
    {
        string json = JsonSerializer.Serialize(data, BeaconApiJson.Options);
        await c.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", c.RequestAborted);
        await c.Response.Body.FlushAsync(c.RequestAborted);
    }

    private sealed record HeadEventDto(
        [property: JsonPropertyName("slot")] string Slot,
        [property: JsonPropertyName("block")] string Block,
        [property: JsonPropertyName("execution_optimistic")] bool ExecutionOptimistic);

    private sealed record FinalizedEventDto(
        [property: JsonPropertyName("epoch")] string Epoch,
        [property: JsonPropertyName("block")] string Block);
}
