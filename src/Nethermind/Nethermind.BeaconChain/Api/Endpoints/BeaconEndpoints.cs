// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary><c>/eth/v1/beacon/*</c>: genesis, headers (by id, slot or parent), block root, block content as SSZ or JSON, the block's attestations, and Gloas execution payload envelopes.</summary>
internal static class BeaconEndpoints
{
    public static void Map(WebApplication app, BeaconApiContext ctx)
    {
        app.MapGet("/eth/v1/beacon/genesis", c => Genesis(c, ctx));
        app.MapGet("/eth/v1/beacon/headers", c => HeaderList(c, ctx.ForRequest()));
        app.MapGet("/eth/v1/beacon/headers/{block_id}", (HttpContext c, string block_id) => HeaderById(c, block_id, ctx.ForRequest()));
        app.MapGet("/eth/v1/beacon/blocks/{block_id}/root", (HttpContext c, string block_id) => BlockRoot(c, block_id, ctx.ForRequest()));
        app.MapGet("/eth/v2/beacon/blocks/{block_id}", (HttpContext c, string block_id) => BlockContent(c, block_id, ctx.ForRequest()));
        app.MapGet("/eth/v2/beacon/blocks/{block_id}/attestations", (HttpContext c, string block_id) => BlockAttestations(c, block_id, ctx.ForRequest()));
        app.MapGet("/eth/v1/beacon/execution_payload_envelopes/{block_id}", (HttpContext c, string block_id) => ExecutionPayloadEnvelope(c, block_id, ctx.ForRequest()));
    }

    /// <summary>beacon-APIs v5.0.0-alpha.2 <c>getSignedExecutionPayloadEnvelope</c>: the verified envelope stored for a Gloas block, as JSON or SSZ.</summary>
    /// <remarks>
    /// Only envelopes that passed verification are stored, so a pre-Gloas block has none, and the store checks that a record names the block it is keyed by.
    /// An envelope is served for any retained block, canonical or not, and whether or not fork choice treats its payload as present.
    /// </remarks>
    private static Task ExecutionPayloadEnvelope(HttpContext c, string blockId, BeaconApiContext ctx)
    {
        ContentNegotiation.ResponseFormat? format = ContentNegotiation.Negotiate(c, sszSupported: true);
        if (format is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        if (!BlockIdResolver.TryResolve(ctx, blockId, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        // An unreadable record throws InvalidDataException, which the host turns into a logged 500.
        if (!ctx.Store.TryGetExecutionPayloadEnvelope(resolved.Root, out SignedExecutionPayloadEnvelope? envelope))
        {
            return ApiErrors.Write(c, StatusCodes.Status404NotFound,
                $"No execution payload envelope is retained for block {resolved.Root}.", c.RequestAborted);
        }

        // gloas/beacon-chain.md process_execution_payload: the envelope's parent_beacon_block_root is its block's parent_root.
        if (envelope.Message!.ParentBeaconBlockRoot != resolved.ParentRoot)
        {
            return ApiErrors.Write(c, StatusCodes.Status500InternalServerError,
                $"The envelope stored for block {resolved.Root} names parent {envelope.Message.ParentBeaconBlockRoot}, not the block's parent {resolved.ParentRoot}.", c.RequestAborted);
        }

        ResponseEnvelope.ApplyConsensusVersionHeader(c, ctx.Spec, resolved.Slot);
        if (format == ContentNegotiation.ResponseFormat.Ssz)
        {
            c.Response.ContentType = ContentNegotiation.OctetStream;
            return c.Response.Body.WriteAsync(SignedExecutionPayloadEnvelope.Encode(envelope), c.RequestAborted).AsTask();
        }

        // types/primitive.yaml ExecutionOptimistic is about the payload served, not the block that committed to it.
        return BeaconApiJson.WriteVersionedEnvelopeAsync(c, ResponseEnvelope.ForkName(BeaconFork.Gloas),
            ResponseEnvelope.PayloadExecutionOptimistic(ctx, resolved),
            ResponseEnvelope.IsFinalized(ctx, resolved.Slot, resolved.Root),
            s =>
            {
                BeaconJsonWriter.WriteSignedExecutionPayloadEnvelope(s.Writer, envelope);
                return Task.CompletedTask;
            });
    }

    /// <summary>beacon-APIs v5.0.0-alpha.2 <c>getBlockAttestationsV2</c>: the attestations included in the block body, in body order.</summary>
    /// <remarks>
    /// The published operation is JSON only and its version enum stops at fulu; a Gloas block is served under
    /// <c>gloas</c> with the Gloas <c>Attestation</c> (consensus-specs v1.7.0-beta.2 gloas/beacon-chain.md), never under a fulu label.
    /// </remarks>
    private static Task BlockAttestations(HttpContext c, string blockId, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        if (!BlockIdResolver.TryResolve(ctx, blockId, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        ulong slot = resolved.Slot;
        BeaconFork fork = ctx.Spec.ForkAtEpoch(ctx.Spec.GetEpoch(slot));
        Action<Utf8JsonWriter> writeAttestations = resolved.Block switch
        {
            ForkedSignedBeaconBlock.OfFulu fulu when fork != BeaconFork.Gloas => w => BeaconJsonWriter.WriteAttestations(w, fulu.Block.Message!.Body!.Attestations!),
            ForkedSignedBeaconBlock.OfGloas gloas when fork == BeaconFork.Gloas => w => BeaconJsonWriter.WriteAttestations(w, gloas.Block.Message!.Body!.Attestations!),
            _ => throw new BeaconStateException(
                $"Block {resolved.Root} at slot {slot} was read as {resolved.Block.GetType().Name}, which is not the shape of the {ResponseEnvelope.ForkName(fork)} fork"),
        };

        ResponseEnvelope.ApplyConsensusVersionHeader(c, ctx.Spec, slot);
        return BeaconApiJson.WriteVersionedEnvelopeAsync(c, ResponseEnvelope.ForkName(fork),
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, slot, resolved.Root),
            s =>
            {
                writeAttestations(s.Writer);
                return Task.CompletedTask;
            });
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
                return BeaconApiJson.WriteEnvelopeAsync(c, Array.Empty<HeaderEntryDto>(), false, false, c.RequestAborted);
            }

            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        HeaderEntryDto entry = BuildHeaderEntry(ctx, resolved);
        ResponseEnvelope.ApplyConsensusVersionHeader(c, ctx.Spec, resolved.Slot);
        return BeaconApiJson.WriteEnvelopeAsync(c, new[] { entry },
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, resolved.Slot, resolved.Root),
            c.RequestAborted);
    }

    /// <summary>
    /// <c>headers?parent_root=...[&amp;slot=...]</c>: every stored child of the parent, from the store's
    /// children index. Every stored block's entry is complete; a stored block without one means the index
    /// is damaged, and is refused because an empty or shorter list would read as a real answer. A parent
    /// that is not stored lists the children the index still holds, or none.
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

        // getBlockHeaders answers a filter that matches nothing with an empty list, so an unknown or pruned parent is not a 404.
        // Read before the index: a block stored later than this has no complete entry to find yet, which is not damage.
        bool stored = ctx.Store.HasBlock(parentRoot!);
        ctx.Store.TryGetChildren(parentRoot!, out Hash256[] childRoots, out bool complete);
        // Only a parent that is still stored can have a damaged entry; a pruned one lists whatever children remain.
        if (stored && !complete && ctx.Store.HasBlock(parentRoot!))
        {
            return ApiErrors.Write(c, StatusCodes.Status500InternalServerError,
                $"The root-to-children index holds no complete entry for the stored block {parentRoot}, so no child list can be served.", c.RequestAborted);
        }

        List<HeaderEntryDto> entries = [];
        bool finalized = childRoots.Length > 0;
        bool optimistic = false;
        BeaconFork? fork = null;
        bool mixedForks = false;
        foreach (Hash256 childRoot in childRoots)
        {
            if (!TryReadChild(ctx, childRoot, slotFilter, out ForkedSignedBeaconBlock? child)) continue;

            ulong childSlot = child.Slot;
            entries.Add(BuildHeaderEntry(ctx, new ResolvedBlock(childRoot, child)));
            finalized &= ResponseEnvelope.IsFinalized(ctx, childSlot, childRoot);
            optimistic |= ResponseEnvelope.ExecutionOptimistic(ctx, childRoot);
            BeaconFork childFork = ctx.Spec.ForkAtEpoch(ctx.Spec.GetEpoch(childSlot));
            mixedForks |= fork is not null && fork != childFork;
            fork = childFork;
        }

        if (fork is not null && !mixedForks)
        {
            c.Response.Headers[ResponseEnvelope.ConsensusVersionHeader] = ResponseEnvelope.ForkName(fork.Value);
        }

        return BeaconApiJson.WriteEnvelopeAsync(c, entries,
            optimistic,
            finalized && entries.Count > 0,
            c.RequestAborted);
    }

    /// <summary>Reads an indexed child that passes <paramref name="slotFilter"/>, decoding it only once its stored slot matches.</summary>
    /// <returns><c>false</c> when the child is filtered out, no longer stored, or unreadable.</returns>
    /// <remarks>
    /// A child pruned after the index was read, or whose record is corrupt, is left out: one bad record
    /// must not turn the children this node can serve into a 500.
    /// </remarks>
    private static bool TryReadChild(BeaconApiContext ctx, Hash256 childRoot, ulong? slotFilter, [NotNullWhen(true)] out ForkedSignedBeaconBlock? child)
    {
        child = null;
        try
        {
            // Read even without a filter: it also bounds the claimed size the full decompression allocates.
            if (!ctx.Store.TryGetBlockSlot(childRoot, out ulong storedSlot) || (slotFilter is not null && storedSlot != slotFilter))
            {
                return false;
            }

            return ctx.Store.TryGetForkedBlock(childRoot, out child);
        }
        catch (Exception e) when (e is BeaconStateException or InvalidDataException)
        {
            ILogger logger = ctx.LogManager.GetClassLogger(typeof(BeaconEndpoints));
            if (logger.IsWarn) logger.Warn($"Beacon API headers?parent_root skipped child {childRoot}: its stored record is unreadable ({e.Message})");
            return false;
        }
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
        ResponseEnvelope.ApplyConsensusVersionHeader(c, ctx.Spec, resolved.Slot);
        return BeaconApiJson.WriteEnvelopeAsync(c, entry,
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, resolved.Slot, resolved.Root),
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

        ResponseEnvelope.ApplyConsensusVersionHeader(c, ctx.Spec, resolved.Slot);
        return BeaconApiJson.WriteEnvelopeAsync(c, new RootDto(resolved.Root.ToString()),
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, resolved.Slot, resolved.Root),
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

        ulong slot = resolved.Slot;
        BeaconFork fork = ctx.Spec.ForkAtEpoch(ctx.Spec.GetEpoch(slot));
        // Chosen before the response starts, so a body in another fork's layout is never served under this fork's name.
        Action<Utf8JsonWriter> writeBlock = resolved.Block switch
        {
            ForkedSignedBeaconBlock.OfFulu fulu when fork != BeaconFork.Gloas => w => BeaconJsonWriter.WriteSignedBeaconBlock(w, fulu.Block),
            ForkedSignedBeaconBlock.OfGloas gloas when fork == BeaconFork.Gloas => w => BeaconJsonWriter.WriteSignedBeaconBlock(w, gloas.Block),
            _ => throw new BeaconStateException(
                $"Block {resolved.Root} at slot {slot} was read as {resolved.Block.GetType().Name}, which is not the shape of the {ResponseEnvelope.ForkName(fork)} fork"),
        };

        ResponseEnvelope.ApplyConsensusVersionHeader(c, ctx.Spec, slot);
        if (format == ContentNegotiation.ResponseFormat.Ssz)
        {
            c.Response.ContentType = ContentNegotiation.OctetStream;
            byte[] encoded = SignedBeaconBlockCodec.Encode(resolved.Block, ctx.Spec);
            return c.Response.Body.WriteAsync(encoded, c.RequestAborted).AsTask();
        }

        return BeaconApiJson.WriteVersionedEnvelopeAsync(c, ResponseEnvelope.ForkName(fork),
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, slot, resolved.Root),
            s =>
            {
                writeBlock(s.Writer);
                return Task.CompletedTask;
            });
    }

    private static HeaderEntryDto BuildHeaderEntry(BeaconApiContext ctx, ResolvedBlock resolved)
    {
        ForkedSignedBeaconBlock block = resolved.Block;
        bool canonical = ctx.Store.TryGetCanonicalRoot(block.Slot, out Hash256? canonicalRoot) && canonicalRoot == resolved.Root;
        Hash256 bodyRoot = resolved.ComputeBodyRoot();

        BeaconBlockHeaderDto header = new(
            block.Slot.ToString(),
            block.ProposerIndex.ToString(),
            block.ParentRoot.ToString(),
            resolved.StateRoot.ToString(),
            bodyRoot.ToString());

        return new HeaderEntryDto(resolved.Root.ToString(), canonical,
            new SignedHeaderDto(header, resolved.Signature.ToString()));
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
