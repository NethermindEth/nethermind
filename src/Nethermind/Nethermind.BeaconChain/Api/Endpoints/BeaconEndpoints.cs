// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary><c>/eth/v1/beacon/*</c>: genesis, headers (by id, slot or parent), block root, block content as SSZ or JSON, the block's attestations, and Gloas execution payload envelopes.</summary>
internal sealed class BeaconEndpoints(BeaconChainSpec spec, BeaconChainStore store, BeaconApiHead view, ILogManager logManager)
{
    /// <summary>beacon-APIs v5.0.0-alpha.2 <c>getSignedExecutionPayloadEnvelope</c>: the verified envelope stored for a Gloas block, as JSON or SSZ.</summary>
    /// <remarks>
    /// Only envelopes that passed verification are stored, so a pre-Gloas block has none, and the store checks that a record names the block it is keyed by.
    /// An envelope is served for any retained block, canonical or not, and whether or not fork choice treats its payload as present.
    /// </remarks>
    [HttpGet("/eth/v1/beacon/execution_payload_envelopes/{blockId}"), Negotiated(Ssz = true)]
    public IResult ExecutionPayloadEnvelope(string blockId)
    {
        if (!BlockIdResolver.TryResolve(store, view, blockId, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiResults.Error(errorStatus, errorMessage!);
        }

        // An unreadable record throws InvalidDataException, which the host turns into a logged 500.
        if (!store.TryGetExecutionPayloadEnvelope(resolved.Root, out SignedExecutionPayloadEnvelope? envelope))
        {
            return ApiResults.Error(StatusCodes.Status404NotFound,
                $"No execution payload envelope is retained for block {resolved.Root}.");
        }

        // gloas/beacon-chain.md process_execution_payload: the envelope's parent_beacon_block_root is its block's parent_root.
        if (envelope.Message!.ParentBeaconBlockRoot != resolved.ParentRoot)
        {
            return ApiResults.Error(StatusCodes.Status500InternalServerError,
                $"The envelope stored for block {resolved.Root} names parent {envelope.Message.ParentBeaconBlockRoot}, not the block's parent {resolved.ParentRoot}.");
        }

        return ApiResults.SszOrJson(
            ssz: () => ApiResults.Ssz(SignedExecutionPayloadEnvelope.Encode(envelope)),
            // types/primitive.yaml ExecutionOptimistic is about the payload served, not the block that committed to it.
            json: () => ApiResults.VersionedEnvelope(ResponseEnvelope.ForkName(BeaconFork.Gloas),
                ResponseEnvelope.PayloadExecutionOptimistic(spec, store, view, resolved),
                ResponseEnvelope.IsFinalized(spec, store, view, resolved.Slot, resolved.Root),
                s =>
                {
                    BeaconJsonWriter.WriteSignedExecutionPayloadEnvelope(s.Writer, envelope);
                    return Task.CompletedTask;
                })).WithConsensusVersion(spec, resolved.Slot);
    }

    /// <summary>beacon-APIs v5.0.0-alpha.2 <c>getBlockAttestationsV2</c>: the attestations included in the block body, in body order.</summary>
    /// <remarks>
    /// The published operation is JSON only and its version enum stops at fulu; a Gloas block is served under
    /// <c>gloas</c> with the Gloas <c>Attestation</c> (consensus-specs v1.7.0-beta.2 gloas/beacon-chain.md), never under a fulu label.
    /// </remarks>
    [HttpGet("/eth/v2/beacon/blocks/{blockId}/attestations"), Negotiated]
    public IResult BlockAttestations(string blockId)
    {
        if (!BlockIdResolver.TryResolve(store, view, blockId, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiResults.Error(errorStatus, errorMessage!);
        }

        ulong slot = resolved.Slot;
        BeaconFork fork = spec.ForkAtEpoch(spec.GetEpoch(slot));
        Action<Utf8JsonWriter> writeAttestations = resolved.Block switch
        {
            ForkedSignedBeaconBlock.OfFulu fulu when fork != BeaconFork.Gloas => w => BeaconJsonWriter.WriteAttestations(w, fulu.Block.Message!.Body!.Attestations!),
            ForkedSignedBeaconBlock.OfGloas gloas when fork == BeaconFork.Gloas => w => BeaconJsonWriter.WriteAttestations(w, gloas.Block.Message!.Body!.Attestations!),
            _ => throw new BeaconStateException(
                $"Block {resolved.Root} at slot {slot} was read as {resolved.Block.GetType().Name}, which is not the shape of the {ResponseEnvelope.ForkName(fork)} fork"),
        };

        return ApiResults.VersionedEnvelope(ResponseEnvelope.ForkName(fork),
            ResponseEnvelope.ExecutionOptimistic(spec, store, view, resolved.Root),
            ResponseEnvelope.IsFinalized(spec, store, view, slot, resolved.Root),
            s =>
            {
                writeAttestations(s.Writer);
                return Task.CompletedTask;
            }).WithConsensusVersion(spec, slot);
    }

    [HttpGet("/eth/v1/beacon/genesis"), Negotiated]
    public IResult Genesis()
    {
        GenesisDto dto = new(
            spec.GenesisTime.ToString(),
            spec.GenesisValidatorsRoot.ToString(),
            spec.Forks[0].Version.ToHexString(withZeroX: true));
        return ApiResults.Data(dto);
    }

    [HttpGet("/eth/v1/beacon/headers"), Negotiated]
    public IResult HeaderList([FromQuery(Name = "parent_root")] string? parentRootRaw, [FromQuery(Name = "slot")] string? slotRaw)
    {
        if (parentRootRaw is not null)
        {
            return HeadersByParent(parentRootRaw, slotRaw);
        }

        string id = slotRaw ?? "head";

        if (!TryResolveHeader(id, out Hash256? root, out SignedBeaconBlockHeader? header, out int errorStatus, out string? errorMessage))
        {
            // A slot with no canonical block is an empty list, not an error - but an outright bad
            // id (or head/finalized genuinely unset) still is.
            if (errorStatus == StatusCodes.Status404NotFound && id != "head" && id != "finalized" && id != "genesis")
            {
                return ApiResults.Envelope(Array.Empty<HeaderEntryDto>(), false, false);
            }

            return ApiResults.Error(errorStatus, errorMessage!);
        }

        HeaderEntryDto entry = BuildHeaderEntry(root, header);
        return ApiResults.Envelope(new[] { entry },
            ResponseEnvelope.ExecutionOptimistic(spec, store, view, root, header.Message!.Slot),
            ResponseEnvelope.IsFinalized(spec, store, view, header.Message!.Slot, root)).WithConsensusVersion(spec, header.Message!.Slot);
    }

    /// <summary>
    /// <c>headers?parent_root=...[&amp;slot=...]</c>: every stored child of the parent, from the store's
    /// children index. Every stored block's entry is complete; a stored block without one means the index
    /// is damaged, and is refused because an empty or shorter list would read as a real answer. A parent
    /// that is not stored lists the children the index still holds, or none.
    /// </summary>
    private IResult HeadersByParent(string parentRootRaw, string? slotRaw)
    {
        if (!HexConvert.TryParseHash(parentRootRaw, out Hash256? parentRoot))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Invalid parent_root: expected a 0x-prefixed 32-byte root.");
        }

        ulong? slotFilter = null;
        if (slotRaw is not null)
        {
            if (!ulong.TryParse(slotRaw, out ulong parsedSlot))
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest, $"Invalid slot '{slotRaw}'.");
            }

            slotFilter = parsedSlot;
        }

        // getBlockHeaders answers a filter that matches nothing with an empty list, so an unknown or pruned parent is not a 404.
        // Read before the index: a block stored later than this has no complete entry to find yet, which is not damage.
        bool stored = store.HasBlock(parentRoot!);
        store.TryGetChildren(parentRoot!, out Hash256[] childRoots, out bool complete);
        // Only a parent that is still stored can have a damaged entry; a pruned one lists whatever children remain.
        if (stored && !complete && store.HasBlock(parentRoot!))
        {
            return ApiResults.Error(StatusCodes.Status500InternalServerError,
                $"The root-to-children index holds no complete entry for the stored block {parentRoot}, so no child list can be served.");
        }

        List<HeaderEntryDto> entries = [];
        bool finalized = childRoots.Length > 0;
        bool optimistic = false;
        BeaconFork? fork = null;
        bool mixedForks = false;
        foreach (Hash256 childRoot in childRoots)
        {
            if (!TryReadChild(childRoot, slotFilter, out SignedBeaconBlockHeader? child)) continue;

            ulong childSlot = child.Message!.Slot;
            entries.Add(BuildHeaderEntry(childRoot, child));
            finalized &= ResponseEnvelope.IsFinalized(spec, store, view, childSlot, childRoot);
            optimistic |= ResponseEnvelope.ExecutionOptimistic(spec, store, view, childRoot, childSlot);
            BeaconFork childFork = spec.ForkAtEpoch(spec.GetEpoch(childSlot));
            mixedForks |= fork is not null && fork != childFork;
            fork = childFork;
        }

        IResult result = ApiResults.Envelope(entries,
            optimistic,
            finalized && entries.Count > 0);
        return fork is not null && !mixedForks ? result.WithConsensusVersion(fork.Value) : result;
    }

    /// <summary>Reads an indexed child that passes <paramref name="slotFilter"/>, decoding it only once its stored slot matches.</summary>
    /// <returns><c>false</c> when the child is filtered out, no longer stored, or unreadable.</returns>
    /// <remarks>
    /// A child pruned after the index was read, or whose record is corrupt, is left out: one bad record
    /// must not turn the children this node can serve into a 500.
    /// </remarks>
    private bool TryReadChild(Hash256 childRoot, ulong? slotFilter, [NotNullWhen(true)] out SignedBeaconBlockHeader? child)
    {
        child = null;
        try
        {
            return store.TryGetSignedHeader(childRoot, slotFilter, out child);
        }
        catch (Exception e) when (e is BeaconStateException or InvalidDataException)
        {
            ILogger logger = logManager.GetClassLogger(typeof(BeaconEndpoints));
            if (logger.IsWarn) logger.Warn($"Beacon API headers?parent_root skipped child {childRoot}: its stored record is unreadable ({e.Message})");
            return false;
        }
    }

    [HttpGet("/eth/v1/beacon/headers/{blockId}"), Negotiated]
    public IResult HeaderById(string blockId)
    {
        if (!TryResolveHeader(blockId, out Hash256? root, out SignedBeaconBlockHeader? header, out int errorStatus, out string? errorMessage))
        {
            return ApiResults.Error(errorStatus, errorMessage!);
        }

        HeaderEntryDto entry = BuildHeaderEntry(root, header);
        return ApiResults.Envelope(entry,
            ResponseEnvelope.ExecutionOptimistic(spec, store, view, root, header.Message!.Slot),
            ResponseEnvelope.IsFinalized(spec, store, view, header.Message!.Slot, root)).WithConsensusVersion(spec, header.Message!.Slot);
    }

    [HttpGet("/eth/v1/beacon/blocks/{blockId}/root"), Negotiated]
    public IResult BlockRoot(string blockId)
    {
        if (!IdResolver.TryResolveRoot(store, view, blockId, out Hash256? root, out int errorStatus, out string? errorMessage))
        {
            return ApiResults.Error(errorStatus, errorMessage!);
        }

        if (!store.TryGetRetainedBlockSlot(root!, out ulong slot))
            return ApiResults.Error(StatusCodes.Status404NotFound, $"Block {root} is not retained by this node.");

        return ApiResults.Envelope(new RootDto(root!.ToString()),
            ResponseEnvelope.ExecutionOptimistic(spec, store, view, root, slot),
            ResponseEnvelope.IsFinalized(spec, store, view, slot, root)).WithConsensusVersion(spec, slot);
    }

    [HttpGet("/eth/v2/beacon/blocks/{blockId}"), Negotiated(Ssz = true)]
    public IResult BlockContent(string blockId)
    {
        if (!BlockIdResolver.TryResolve(store, view, blockId, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiResults.Error(errorStatus, errorMessage!);
        }

        ulong slot = resolved.Slot;
        BeaconFork fork = spec.ForkAtEpoch(spec.GetEpoch(slot));
        // Chosen before the response starts, so a body in another fork's layout is never served under this fork's name.
        Action<Utf8JsonWriter> writeBlock = resolved.Block switch
        {
            ForkedSignedBeaconBlock.OfFulu fulu when fork != BeaconFork.Gloas => w => BeaconJsonWriter.WriteSignedBeaconBlock(w, fulu.Block),
            ForkedSignedBeaconBlock.OfGloas gloas when fork == BeaconFork.Gloas => w => BeaconJsonWriter.WriteSignedBeaconBlock(w, gloas.Block),
            _ => throw new BeaconStateException(
                $"Block {resolved.Root} at slot {slot} was read as {resolved.Block.GetType().Name}, which is not the shape of the {ResponseEnvelope.ForkName(fork)} fork"),
        };

        return ApiResults.SszOrJson(
            ssz: () => ApiResults.Ssz(SignedBeaconBlockCodec.Encode(resolved.Block, spec)),
            json: () => ApiResults.VersionedEnvelope(ResponseEnvelope.ForkName(fork),
                ResponseEnvelope.ExecutionOptimistic(spec, store, view, resolved.Root),
                ResponseEnvelope.IsFinalized(spec, store, view, slot, resolved.Root),
                s =>
                {
                    writeBlock(s.Writer);
                    return Task.CompletedTask;
                })).WithConsensusVersion(spec, slot);
    }

    private bool TryResolveHeader(string id, [NotNullWhen(true)] out Hash256? root,
        [NotNullWhen(true)] out SignedBeaconBlockHeader? header, out int errorStatus, out string? errorMessage)
    {
        header = null;
        if (!IdResolver.TryResolveRoot(store, view, id, out root, out errorStatus, out errorMessage) || root is null) return false;
        if (store.TryGetSignedHeader(root, null, out header)) return true;
        errorStatus = StatusCodes.Status404NotFound;
        errorMessage = $"Block {root} is not retained by this node.";
        return false;
    }

    private HeaderEntryDto BuildHeaderEntry(Hash256 root, SignedBeaconBlockHeader signed)
    {
        BeaconBlockHeader message = signed.Message!;
        bool canonical = store.TryGetCanonicalRoot(message.Slot, out Hash256? canonicalRoot) && canonicalRoot == root;
        BeaconBlockHeaderDto header = new(message.Slot.ToString(), message.ProposerIndex.ToString(),
            message.ParentRoot!.ToString(), message.StateRoot!.ToString(), message.BodyRoot!.ToString());
        return new HeaderEntryDto(root.ToString(), canonical, new SignedHeaderDto(header, signed.Signature.ToString()));
    }

    private sealed record GenesisDto(
        [property: JsonPropertyName("genesis_time")] string GenesisTime,
        [property: JsonPropertyName("genesis_validators_root")] string GenesisValidatorsRoot,
        [property: JsonPropertyName("genesis_fork_version")] string GenesisForkVersion);

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
