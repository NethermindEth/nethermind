// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary>
/// <c>/eth/v1/events</c>: server-sent events for <c>head</c> and <c>finalized_checkpoint</c>.
/// </summary>
/// <param name="ctx">The host's own context, not one request's view of it: a stream captures a new view at each poll.</param>
internal sealed class EventsEndpoint(BeaconApiContext ctx, IHostApplicationLifetime lifetime)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly string[] SupportedTopics = ["head", "finalized_checkpoint"];

    private readonly EventCache _events = new();
    private int _activeStreams;

    [HttpGet("/eth/v1/events")]
    public async Task Stream(HttpContext c)
    {
        CancellationToken stopping = lifetime.ApplicationStopping;
        if (Interlocked.Increment(ref _activeStreams) > 32)
        {
            Interlocked.Decrement(ref _activeStreams);
            await ApiErrors.Write(c, StatusCodes.Status503ServiceUnavailable, "Too many event streams.", c.RequestAborted);
            return;
        }
        try
        {
            using CancellationTokenSource streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted, stopping);
            c.RequestAborted = streamLifetime.Token;
            await StreamCore(c);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested || c.RequestAborted.IsCancellationRequested) { }
        finally { Interlocked.Decrement(ref _activeStreams); }
    }

    private async Task StreamCore(HttpContext c)
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
        ulong? lastHeadSlot = null;
        ulong lastFinalizedEpoch = ulong.MaxValue;
        try
        {
            while (!c.RequestAborted.IsCancellationRequested)
            {
                BeaconApiContext poll = ctx.ForRequest();
                StatusMessageV2 status = poll.StatusSource.CurrentStatus;
                if (wantsHead && status.HeadRoot != Hash256.Zero && status.HeadRoot != lastHead
                    && _events.Head(poll, status.HeadRoot!, lastHeadSlot) is { } headEvent)
                {
                    lastHead = status.HeadRoot;
                    lastHeadSlot = status.HeadSlot;
                    await WriteEventAsync(c, "head", headEvent);
                }

                if (wantsFinalized && status.FinalizedRoot != Hash256.Zero && status.FinalizedEpoch != lastFinalizedEpoch
                    && BlockIdResolver.TryResolve(poll, status.FinalizedRoot!.ToString(), out ResolvedBlock finalized, out _, out _))
                {
                    lastFinalizedEpoch = status.FinalizedEpoch;
                    await WriteEventAsync(c, "finalized_checkpoint",
                        CreateFinalizedEvent(poll, status.FinalizedEpoch, finalized));
                }

                await Task.Delay(PollInterval, c.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected or the process is shutting down; nothing left to write.
        }
    }

    internal static HeadEventDto? CreateHeadEvent(BeaconApiContext ctx, Hash256 root, ulong? previousSlot)
    {
        if (!BlockIdResolver.TryResolve(ctx, root.ToString(), out ResolvedBlock head, out _, out _)) return null;
        ulong epoch = ctx.Spec.GetEpoch(head.Slot);
        if (!TryDependentRoot(ctx, head, epoch, out Hash256? currentRoot)
            || !TryDependentRoot(ctx, head, epoch == 0 ? 0 : epoch - 1, out Hash256? previousRoot)) return null;
        bool epochTransition = previousSlot is { } slot
            ? epoch != ctx.Spec.GetEpoch(slot) : head.Slot % ctx.Spec.SlotsPerEpoch == 0;
        return new HeadEventDto(head.Slot.ToString(), root.ToString(), head.StateRoot.ToString(), epochTransition,
            previousRoot!.ToString(), currentRoot!.ToString(), ResponseEnvelope.ExecutionOptimistic(ctx, root));
    }

    internal static FinalizedEventDto CreateFinalizedEvent(BeaconApiContext ctx, ulong epoch, ResolvedBlock block) =>
        new(epoch.ToString(), block.Root.ToString(), block.StateRoot.ToString(), ResponseEnvelope.ExecutionOptimistic(ctx, block.Root));

    private static bool TryDependentRoot(BeaconApiContext ctx, ResolvedBlock head, ulong epoch, out Hash256? root)
    {
        if (ctx.ForkChoiceSnapshot is { } snapshot)
        {
            root = ProposerLookaheadSnapshot.FindDependentRoot(snapshot.Nodes, head.Root, epoch * ctx.Spec.SlotsPerEpoch);
            foreach (ForkChoiceSnapshotNode node in snapshot.Nodes)
                if (node.Root == root && node.Slot < epoch * ctx.Spec.SlotsPerEpoch) return true;
        }
        // The dependent root can predate the retained fork-choice tree.
        if (epoch == 0) return ctx.Store.TryGetCanonicalRoot(0, out root);
        ulong targetSlot = epoch * ctx.Spec.SlotsPerEpoch - 1;
        ResolvedBlock ancestor = head;
        while (ancestor.Slot > targetSlot)
        {
            if (!BlockIdResolver.TryResolve(ctx, ancestor.ParentRoot.ToString(), out ResolvedBlock parent, out _, out _)
                || parent.Slot >= ancestor.Slot)
            {
                root = null;
                return false;
            }
            ancestor = parent;
        }
        root = ancestor.Root;
        return true;
    }

    private static async Task WriteEventAsync<T>(HttpContext c, string eventName, T data)
    {
        string json = JsonSerializer.Serialize(data, BeaconApiJson.Options);
        await c.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", c.RequestAborted);
        await c.Response.Body.FlushAsync(c.RequestAborted);
    }

    internal sealed class EventCache
    {
        private readonly Lock _lock = new();
        private Hash256? _root;
        private HeadEventDto? _head;
        private ulong _attemptSlot;

        public HeadEventDto? Head(BeaconApiContext ctx, Hash256 root, ulong? previousSlot)
        {
            lock (_lock)
            {
                ulong currentSlot = ctx.SlotClock.CurrentSlot;
                if (_root != root || (_head is null && _attemptSlot != currentSlot))
                {
                    _head = CreateHeadEvent(ctx, root, null);
                    _root = root;
                    _attemptSlot = currentSlot;
                }
                if (_head is not { } head) return null;
                return head with
                {
                    EpochTransition = previousSlot is { } previous
                        ? ctx.Spec.GetEpoch(ctx.StatusSource.CurrentStatus.HeadSlot) != ctx.Spec.GetEpoch(previous) : head.EpochTransition,
                    ExecutionOptimistic = ResponseEnvelope.ExecutionOptimistic(ctx, root),
                };
            }
        }
    }

    internal sealed record HeadEventDto(
        [property: JsonPropertyName("slot")] string Slot,
        [property: JsonPropertyName("block")] string Block,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("epoch_transition")] bool EpochTransition,
        [property: JsonPropertyName("previous_duty_dependent_root")] string PreviousDutyDependentRoot,
        [property: JsonPropertyName("current_duty_dependent_root")] string CurrentDutyDependentRoot,
        [property: JsonPropertyName("execution_optimistic")] bool ExecutionOptimistic);

    internal sealed record FinalizedEventDto(
        [property: JsonPropertyName("epoch")] string Epoch,
        [property: JsonPropertyName("block")] string Block,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("execution_optimistic")] bool ExecutionOptimistic);
}
