// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary>beacon-APIs v5.0.0-alpha.2 <c>getBlobs</c>: the blobs of a block, rebuilt from the data columns this node stores.</summary>
/// <remarks>
/// The node keeps data column sidecars, not blobs. apis/beacon/blobs/blobs.yaml: after Fulu only a node that custodies every
/// column must serve blobs, and others may rebuild them; this node rebuilds them from any half of the columns
/// (fulu/das-core.md recover_matrix), and answers 404 when it holds fewer.
/// </remarks>
internal static class BlobsEndpoint
{
    /// <summary>The most blob rebuilds that run at once; each reads up to half of a block's columns and may run cell recovery per blob.</summary>
    internal const int MaxConcurrentRebuilds = 2;

    public static void Map(WebApplication app, BeaconApiContext ctx) =>
        app.MapGet("/eth/v1/beacon/blobs/{block_id}", (HttpContext c, string block_id) => GetBlobs(c, block_id, ctx.ForRequest()));

    /// <summary>Serves the block's blobs, all of them or those named by <c>versioned_hashes</c>, in the block's commitment order, as JSON or SSZ.</summary>
    /// <remarks>
    /// A busy node answers 503 at once, as the state routes do: blobs.yaml lists no such status, and the response keeps the spec's
    /// <c>ErrorMessage</c> shape. A stored column whose cell count differs from the block's commitment count is damage, a 500.
    /// </remarks>
    internal static async Task GetBlobs(HttpContext c, string blockId, BeaconApiContext ctx)
    {
        ContentNegotiation.ResponseFormat? format = ContentNegotiation.Negotiate(c, sszSupported: true);
        if (format is null)
        {
            await ContentNegotiation.WriteNotAcceptable(c);
            return;
        }

        if (!BlockIdResolver.TryResolve(ctx, blockId, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage))
        {
            await ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
            return;
        }

        if (!TryParseVersionedHashes(c.Request.Query["versioned_hashes"], out HashSet<Hash256>? wanted, out string? queryError))
        {
            await ApiErrors.Write(c, StatusCodes.Status400BadRequest, queryError!, c.RequestAborted);
            return;
        }

        (SszKzgCommitment[] commitments, bool gloas) = resolved.Block switch
        {
            ForkedSignedBeaconBlock.OfFulu fulu => (fulu.Block.Message!.Body!.BlobKzgCommitments ?? [], false),
            ForkedSignedBeaconBlock.OfGloas gloasBlock => (gloasBlock.Block.Message!.Body!.SignedExecutionPayloadBid!.Message!.BlobKzgCommitments ?? [], true),
            _ => throw new BeaconStateException($"Block {resolved.Root} was read as {resolved.Block.GetType().Name}, which carries no blob commitments"),
        };

        List<int> rows = SelectRows(commitments, wanted);
        byte[][] blobs = [];
        if (rows.Count > 0)
        {
            using RateLimitLease lease = ctx.BlobRebuilds.AttemptAcquire();
            if (!lease.IsAcquired)
            {
                await ApiErrors.Write(c, StatusCodes.Status503ServiceUnavailable,
                    $"The node is already rebuilding blobs for {MaxConcurrentRebuilds} requests; retry later.", c.RequestAborted);
                return;
            }

            SszBlobCell[]?[] cellsByColumn = ReadColumns(ctx, resolved.Root, gloas, commitments.Length, out int held);
            if (held < Eip7594DasConstants.RequiredColumnsForReconstruction)
            {
                await ApiErrors.Write(c, StatusCodes.Status404NotFound,
                    $"Block {resolved.Root} has {commitments.Length} blobs, but this node holds {held} of its {Eip7594DasConstants.NumberOfColumns} data columns and needs {Eip7594DasConstants.RequiredColumnsForReconstruction} to rebuild them.",
                    c.RequestAborted);
                return;
            }

            if (!DataColumnReconstruction.TryRecoverBlobs(cellsByColumn, rows, out blobs))
            {
                throw new InvalidDataException($"The stored data columns of block {resolved.Root} did not recover into blobs");
            }
        }

        if (format == ContentNegotiation.ResponseFormat.Ssz)
        {
            // List[Blob, MAX_BLOB_COMMITMENTS_PER_BLOCK]: fixed-size elements, so the encoding is the blobs back to back.
            c.Response.ContentType = ContentNegotiation.OctetStream;
            foreach (byte[] blob in blobs)
            {
                await c.Response.Body.WriteAsync(blob, c.RequestAborted);
            }

            return;
        }

        c.Response.ContentType = ContentNegotiation.Json;
        await using BeaconJsonStream json = new(c.Response.BodyWriter, c.RequestAborted);
        Utf8JsonWriter w = json.Writer;
        w.WriteStartObject();
        // The blobs are those of the block's execution payload, which under EIP-7732 the bid only commits to.
        w.WriteBoolean("execution_optimistic", ResponseEnvelope.PayloadExecutionOptimistic(ctx, resolved));
        w.WriteBoolean("finalized", ResponseEnvelope.IsFinalized(ctx, resolved.Slot, resolved.Root));
        w.WriteStartArray("data");
        foreach (byte[] blob in blobs)
        {
            BeaconJsonWriter.WriteHexValue(w, blob);
            await json.CheckpointAsync();
        }

        w.WriteEndArray();
        w.WriteEndObject();
        await json.FlushAsync();
    }

    /// <summary>Parses <c>versioned_hashes</c>, given as repeated keys or comma-separated; <c>null</c> when absent, which selects every blob.</summary>
    /// <remarks>blobs.yaml declares the array <c>uniqueItems</c>, so a repeated hash is refused like a malformed one.</remarks>
    private static bool TryParseVersionedHashes(StringValues values, out HashSet<Hash256>? wanted, out string? error)
    {
        wanted = null;
        error = null;
        if (values.Count == 0)
        {
            return true;
        }

        wanted = [];
        foreach (string? value in values)
        {
            foreach (string part in (value ?? "").Split(','))
            {
                if (!HexConvert.TryParseHash(part.Trim(), out Hash256? hash))
                {
                    error = $"Invalid versioned_hashes entry '{part}': expected a 0x-prefixed 32-byte hash.";
                    return false;
                }

                if (!wanted.Add(hash!))
                {
                    error = $"versioned_hashes lists {hash} more than once.";
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The blob indices to serve, in commitment order: all of them, or those whose <c>kzg_to_versioned_hash</c> is in <paramref name="wanted"/>.</summary>
    private static List<int> SelectRows(SszKzgCommitment[] commitments, HashSet<Hash256>? wanted)
    {
        List<int> rows = new(commitments.Length);
        Hash256?[] versionedHashes = wanted is null ? [] : PayloadConverter.ToBlobVersionedHashes(commitments);
        for (int i = 0; i < commitments.Length; i++)
        {
            if (wanted is null || wanted.Contains(versionedHashes[i]!))
            {
                rows.Add(i);
            }
        }

        return rows;
    }

    /// <summary>Reads accepted columns of the block in index order until half of them are held, so columns 0 to 63 are preferred.</summary>
    /// <exception cref="InvalidDataException">A stored column is unreadable, or its cell count is not <paramref name="blobCount"/>.</exception>
    private static SszBlobCell[]?[] ReadColumns(BeaconApiContext ctx, Hash256 root, bool gloas, int blobCount, out int held)
    {
        SszBlobCell[]?[] cellsByColumn = new SszBlobCell[]?[Eip7594DasConstants.NumberOfColumns];
        held = 0;
        for (ulong column = 0; column < Eip7594DasConstants.NumberOfColumns && held < Eip7594DasConstants.RequiredColumnsForReconstruction; column++)
        {
            // Read queued columns first: the writer removes them only after persistence completes.
            SszBlobCell[]? cells = ctx.ColumnPool?.GetUnwrittenCells(root, column, gloas);
            cells ??= gloas
                ? ctx.Store.TryGetDataColumnSidecarGloas(root, column, out DataColumnSidecarGloas? gloasSidecar) ? gloasSidecar.Column ?? [] : null
                : ctx.Store.TryGetDataColumnSidecar(root, column, out DataColumnSidecar? sidecar) ? sidecar.Column ?? [] : null;
            if (cells is null)
            {
                continue;
            }

            if (cells.Length != blobCount)
            {
                throw new InvalidDataException($"The data column {column} stored for block {root} has {cells.Length} cells, not one per each of its {blobCount} blobs");
            }

            cellsByColumn[column] = cells;
            held++;
        }

        return cellsByColumn;
    }
}
