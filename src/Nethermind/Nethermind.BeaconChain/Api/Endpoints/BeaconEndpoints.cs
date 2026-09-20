// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary><c>/eth/v1/beacon/*</c>: genesis, headers, block root, and (SSZ-only) block content.</summary>
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

        if (c.Request.Query.ContainsKey("parent_root"))
        {
            // Would need a root -> children index this store does not maintain (it only indexes
            // canonical-slot -> root); answering "no matches" would be indistinguishable from a
            // genuine empty result, so this is refused rather than silently ignored.
            return ApiErrors.Write(c, StatusCodes.Status501NotImplemented,
                "Filtering headers by parent_root is not implemented: the store has no root-to-children index.", c.RequestAborted);
        }

        string id = c.Request.Query.TryGetValue("slot", out Microsoft.Extensions.Primitives.StringValues slotValue)
            ? slotValue.ToString()
            : "head";

        if (!BlockIdResolver.TryResolve(ctx, id, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage))
        {
            // A slot with no canonical block is an empty list, not an error — but an outright bad
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

        ResponseEnvelope.ApplyConsensusVersionHeader(c, ctx.Spec, resolved.Block.Message!.Slot);
        if (format == ContentNegotiation.ResponseFormat.Ssz)
        {
            c.Response.ContentType = ContentNegotiation.OctetStream;
            byte[] encoded = SignedBeaconBlock.Encode(resolved.Block);
            return c.Response.Body.WriteAsync(encoded, c.RequestAborted).AsTask();
        }

        // JSON serialization of the full, fork-specific block body (hundreds of SSZ fields across
        // Electra/Fulu/Gloas) is not implemented; use `Accept: application/octet-stream` for the
        // SSZ representation this endpoint does serve.
        return ApiErrors.Write(c, StatusCodes.Status501NotImplemented,
            "JSON block bodies are not implemented; request 'Accept: application/octet-stream' for SSZ.", c.RequestAborted);
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
