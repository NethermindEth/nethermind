// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary><c>/eth/v1/beacon/*</c>: genesis, headers (by id, slot or parent), block root, and block content as SSZ or JSON.</summary>
internal static class BeaconEndpoints
{
    public static void Map(WebApplication app, BeaconApiContext ctx)
    {
        app.MapGet("/eth/v1/beacon/genesis", c => Genesis(c, ctx));
        app.MapGet("/eth/v1/beacon/headers", c => HeaderList(c, ctx));
        app.MapGet("/eth/v1/beacon/headers/{block_id}", (HttpContext c, string block_id) => HeaderById(c, block_id, ctx));
        app.MapGet("/eth/v1/beacon/blocks/{block_id}/root", (HttpContext c, string block_id) => BlockRoot(c, block_id, ctx));
        app.MapGet("/eth/v2/beacon/blocks/{block_id}", (HttpContext c, string block_id) => BlockContent(c, block_id, ctx));
    }

    private static Task Genesis(HttpContext c, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        GenesisDto dto = new(
            ctx.Spec.GenesisTime.ToString(),
            ctx.Spec.GenesisValidatorsRoot.ToString(),
            ctx.Spec.Forks[0].Version.ToHexString(withZeroX: true));
        return BeaconApiJson.WriteDataAsync(c, dto, c.RequestAborted);
    }

    private static Task HeaderList(HttpContext c, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        if (c.Request.Query.TryGetValue("parent_root", out Microsoft.Extensions.Primitives.StringValues parentRootValue))
        {
            return HeadersByParent(c, parentRootValue.ToString(), ctx);
        }

        string id = c.Request.Query.TryGetValue("slot", out Microsoft.Extensions.Primitives.StringValues slotValue)
            ? slotValue.ToString()
            : "head";

        if (!BlockIdResolver.TryResolve(ctx, id, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage))
        {
            // A slot with no canonical block is an empty list, not an error - but an outright bad
            // id (or head/finalized genuinely unset) still is.
            if (errorStatus == StatusCodes.Status404NotFound && id != "head" && id != "finalized" && id != "genesis")
            {
                return BeaconApiJson.WriteEnvelopeAsync(c, Array.Empty<HeaderEntryDto>(), ResponseEnvelope.ExecutionOptimistic(), false, c.RequestAborted);
            }

            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        HeaderEntryDto entry = BuildHeaderEntry(ctx, resolved);
        ResponseEnvelope.ApplyConsensusVersionHeader(c, ctx.Spec, resolved.Block.Message!.Slot);
        return BeaconApiJson.WriteEnvelopeAsync(c, new[] { entry },
            ResponseEnvelope.ExecutionOptimistic(),
            ResponseEnvelope.IsFinalized(ctx.Spec, ctx.StatusSource, resolved.Block.Message.Slot),
            c.RequestAborted);
    }

    /// <summary>
    /// <c>headers?parent_root=...[&amp;slot=...]</c>: every stored child of the parent, from the store's
    /// children index. Served only when that index is complete for the parent; a parent stored before
    /// the index existed is refused, because an empty or shorter list would read as a real answer.
    /// </summary>
    private static Task HeadersByParent(HttpContext c, string parentRootRaw, BeaconApiContext ctx)
    {
        if (!HexConvert.TryParseHash(parentRootRaw, out Hash256? parentRoot))
        {
            return ApiErrors.Write(c, StatusCodes.Status400BadRequest, "Invalid parent_root: expected a 0x-prefixed 32-byte root.", c.RequestAborted);
        }

        ulong? slotFilter = null;
        if (c.Request.Query.TryGetValue("slot", out Microsoft.Extensions.Primitives.StringValues slotRaw))
        {
            if (!ulong.TryParse(slotRaw.ToString(), out ulong parsedSlot))
            {
                return ApiErrors.Write(c, StatusCodes.Status400BadRequest, $"Invalid slot '{slotRaw}'.", c.RequestAborted);
            }

            slotFilter = parsedSlot;
        }

        if (!ctx.Store.TryGetBlock(parentRoot!, out _))
        {
            return ApiErrors.Write(c, StatusCodes.Status404NotFound, $"Block {parentRoot} is not retained by this node.", c.RequestAborted);
        }

        if (!ctx.Store.TryGetChildren(parentRoot!, out Hash256[] childRoots, out bool complete) || !complete)
        {
            return ApiErrors.Write(c, StatusCodes.Status501NotImplemented,
                $"Children of block {parentRoot} are not indexed: it was stored before this node kept a root-to-children index, so any list would be incomplete.", c.RequestAborted);
        }

        List<HeaderEntryDto> entries = [];
        bool finalized = childRoots.Length > 0;
        BeaconFork? fork = null;
        bool mixedForks = false;
        foreach (Hash256 childRoot in childRoots)
        {
            if (!ctx.Store.TryGetBlock(childRoot, out SignedBeaconBlock? child))
            {
                // The index and the block column disagree (a prune raced this request); skipping the
                // entry would hand back a shorter list that looks complete.
                return ApiErrors.Write(c, StatusCodes.Status500InternalServerError,
                    $"The children index names block {childRoot}, which is not retained; retry the request.", c.RequestAborted);
            }

            ulong childSlot = child.Message!.Slot;
            if (slotFilter is not null && childSlot != slotFilter) continue;

            entries.Add(BuildHeaderEntry(ctx, new ResolvedBlock(childRoot, child)));
            finalized &= ResponseEnvelope.IsFinalized(ctx.Spec, ctx.StatusSource, childSlot);
            BeaconFork childFork = ctx.Spec.ForkAtEpoch(ctx.Spec.GetEpoch(childSlot));
            mixedForks |= fork is not null && fork != childFork;
            fork = childFork;
        }

        if (fork is not null && !mixedForks)
        {
            c.Response.Headers[ResponseEnvelope.ConsensusVersionHeader] = ResponseEnvelope.ForkName(fork.Value);
        }

        return BeaconApiJson.WriteEnvelopeAsync(c, entries,
            ResponseEnvelope.ExecutionOptimistic(),
            finalized && entries.Count > 0,
            c.RequestAborted);
    }

    private static Task HeaderById(HttpContext c, string blockId, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        if (!BlockIdResolver.TryResolve(ctx, blockId, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        HeaderEntryDto entry = BuildHeaderEntry(ctx, resolved);
        ResponseEnvelope.ApplyConsensusVersionHeader(c, ctx.Spec, resolved.Block.Message!.Slot);
        return BeaconApiJson.WriteEnvelopeAsync(c, entry,
            ResponseEnvelope.ExecutionOptimistic(),
            ResponseEnvelope.IsFinalized(ctx.Spec, ctx.StatusSource, resolved.Block.Message.Slot),
            c.RequestAborted);
    }

    private static Task BlockRoot(HttpContext c, string blockId, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        if (!BlockIdResolver.TryResolve(ctx, blockId, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        ResponseEnvelope.ApplyConsensusVersionHeader(c, ctx.Spec, resolved.Block.Message!.Slot);
        return BeaconApiJson.WriteEnvelopeAsync(c, new RootDto(resolved.Root.ToString()),
            ResponseEnvelope.ExecutionOptimistic(),
            ResponseEnvelope.IsFinalized(ctx.Spec, ctx.StatusSource, resolved.Block.Message.Slot),
            c.RequestAborted);
    }

    private static Task BlockContent(HttpContext c, string blockId, BeaconApiContext ctx)
    {
        if (!BlockIdResolver.TryResolve(ctx, blockId, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        ContentNegotiation.ResponseFormat? format = ContentNegotiation.Negotiate(c, sszSupported: true);
        if (format is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        ulong slot = resolved.Block.Message!.Slot;
        ResponseEnvelope.ApplyConsensusVersionHeader(c, ctx.Spec, slot);
        if (format == ContentNegotiation.ResponseFormat.Ssz)
        {
            c.Response.ContentType = ContentNegotiation.OctetStream;
            byte[] encoded = SignedBeaconBlock.Encode(resolved.Block);
            return c.Response.Body.WriteAsync(encoded, c.RequestAborted).AsTask();
        }

        BeaconFork fork = ctx.Spec.ForkAtEpoch(ctx.Spec.GetEpoch(slot));
        if (fork == BeaconFork.Gloas)
        {
            // The store decodes every block with the Electra/Fulu body layout; a Gloas body (no
            // execution_payload, a signed bid instead) would come out as a plausible wrong object.
            return ApiErrors.Write(c, StatusCodes.Status501NotImplemented,
                $"JSON bodies for {ResponseEnvelope.ForkName(fork)} blocks are not implemented; only the Electra/Fulu body layout is serialized.", c.RequestAborted);
        }

        return BeaconApiJson.WriteVersionedEnvelopeAsync(c, ResponseEnvelope.ForkName(fork),
            ResponseEnvelope.ExecutionOptimistic(),
            ResponseEnvelope.IsFinalized(ctx.Spec, ctx.StatusSource, slot),
            s =>
            {
                BeaconJsonWriter.WriteSignedBeaconBlock(s.Writer, resolved.Block);
                return Task.CompletedTask;
            });
    }

    private static HeaderEntryDto BuildHeaderEntry(BeaconApiContext ctx, ResolvedBlock resolved)
    {
        BeaconBlock message = resolved.Block.Message!;
        bool canonical = ctx.Store.TryGetCanonicalRoot(message.Slot, out Hash256? canonicalRoot) && canonicalRoot == resolved.Root;
        Hash256 bodyRoot = SszRoots.HashTreeRoot(message.Body!);

        BeaconBlockHeaderDto header = new(
            message.Slot.ToString(),
            message.ProposerIndex.ToString(),
            message.ParentRoot!.ToString(),
            message.StateRoot!.ToString(),
            bodyRoot.ToString());

        return new HeaderEntryDto(resolved.Root.ToString(), canonical,
            new SignedHeaderDto(header, resolved.Block.Signature.ToString()));
    }

    private sealed record GenesisDto(
        [property: JsonPropertyName("genesis_time")] string GenesisTime,
        [property: JsonPropertyName("genesis_validators_root")] string GenesisValidatorsRoot,
        [property: JsonPropertyName("genesis_fork_version")] string GenesisForkVersion);

    private sealed record RootDto([property: JsonPropertyName("root")] string Root);

    private sealed record BeaconBlockHeaderDto(
        [property: JsonPropertyName("slot")] string Slot,
        [property: JsonPropertyName("proposer_index")] string ProposerIndex,
        [property: JsonPropertyName("parent_root")] string ParentRoot,
        [property: JsonPropertyName("state_root")] string StateRoot,
        [property: JsonPropertyName("body_root")] string BodyRoot);

    private sealed record SignedHeaderDto(
        [property: JsonPropertyName("message")] BeaconBlockHeaderDto Message,
        [property: JsonPropertyName("signature")] string Signature);

    private sealed record HeaderEntryDto(
        [property: JsonPropertyName("root")] string Root,
        [property: JsonPropertyName("canonical")] bool Canonical,
        [property: JsonPropertyName("header")] SignedHeaderDto Header);
}
