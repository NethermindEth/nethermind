// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Eez.Execution.Settlement.RollupX;

namespace Nethermind.Eez.Follower.RollupX;

/// <summary>Reads the blobs an L1 transaction carried.</summary>
public interface IL1BlobSource
{
    /// <returns>The blobs <paramref name="versionedHashes"/> commit to, in their order.</returns>
    /// <exception cref="L1SourceIncompleteException">The blobs cannot be served yet, or are no longer served.</exception>
    Task<byte[][]> GetBlobs(EezL1Block block, IReadOnlyList<Hash256> versionedHashes, CancellationToken token);
}

/// <summary>
/// Reads blobs from a beacon node: every blob of the block's slot, from <c>blobs</c> or, on a node without it,
/// <c>blob_sidecars</c>. Nothing the node says about a blob is trusted: each is matched to a transaction's versioned
/// hash by recomputing its commitment, which binds the exact bytes.
/// </summary>
/// <remarks>Does not own <paramref name="http"/>; the plugin manages its lifetime.</remarks>
public sealed class BeaconBlobSource(HttpClient http) : IL1BlobSource
{
    private const int MaxSlotBlobs = 128;
    private const int MaxBlobResponseBytes = 40 * 1024 * 1024;
    private const int MaxClockResponseBytes = 64 * 1024;

    private (ulong GenesisTime, ulong SecondsPerSlot)? _clock;

    public async Task<byte[][]> GetBlobs(EezL1Block block, IReadOnlyList<Hash256> versionedHashes, CancellationToken token)
    {
        if (versionedHashes.Count == 0)
        {
            return [];
        }

        await KzgPolynomialCommitments.InitializeAsync();
        ulong slot = await Slot(block, token);
        List<byte[]> slotBlobs = await SlotBlobs(slot, token);
        try
        {
            return BlobSelection.Select(slotBlobs, versionedHashes)
                ?? throw new L1SourceIncompleteException(block.Number, $"The beacon node does not serve every blob L1 block {block.Number} carried in slot {slot}.");
        }
        catch (ArgumentException)
        {
            throw new L1SourceIncompleteException(block.Number, $"The beacon node serves an invalid blob field element for slot {slot}.");
        }
    }

    private async Task<ulong> Slot(EezL1Block block, CancellationToken token)
    {
        (ulong genesisTime, ulong secondsPerSlot) = _clock ??= await Clock(token);
        if (block.Timestamp < genesisTime || (block.Timestamp - genesisTime) % secondsPerSlot != 0)
        {
            throw new EezFollowerException(
                $"L1 block {block.Number} at {block.Timestamp} is off the beacon chain's slots from {genesisTime} every {secondsPerSlot} s: the beacon node serves another chain.");
        }

        return (block.Timestamp - genesisTime) / secondsPerSlot;
    }

    private async Task<(ulong, ulong)> Clock(CancellationToken token)
    {
        using JsonDocument genesis = await Get("eth/v1/beacon/genesis", MaxClockResponseBytes, token)
            ?? throw new L1SourceIncompleteException(0, "The beacon node does not serve its genesis.");
        using JsonDocument spec = await Get("eth/v1/config/spec", MaxClockResponseBytes, token)
            ?? throw new L1SourceIncompleteException(0, "The beacon node does not serve its spec.");
        ulong genesisTime = Quantity(Property(Property(genesis.RootElement, "data"), "genesis_time"));
        ulong secondsPerSlot = Quantity(Property(Property(spec.RootElement, "data"), "SECONDS_PER_SLOT"));
        return secondsPerSlot == 0 ? throw Incomplete("zero seconds per slot") : (genesisTime, secondsPerSlot);
    }

    private async Task<List<byte[]>> SlotBlobs(ulong slot, CancellationToken token)
    {
        List<byte[]> blobs = [];
        using (JsonDocument? fulu = await Get($"eth/v1/beacon/blobs/{slot}", MaxBlobResponseBytes, token))
        {
            if (fulu is not null)
            {
                foreach (JsonElement blob in Data(fulu))
                {
                    blobs.Add(Blob(blob));
                }

                return blobs;
            }
        }

        using JsonDocument sidecars = await Get($"eth/v1/beacon/blob_sidecars/{slot}", MaxBlobResponseBytes, token)
            ?? throw new L1SourceIncompleteException(0, $"The beacon node serves no blobs for slot {slot}.");
        foreach (JsonElement sidecar in Data(sidecars))
        {
            blobs.Add(Blob(Property(sidecar, "blob")));
        }

        return blobs;
    }

    /// <returns>The response, or <see langword="null"/> when the node does not have the endpoint.</returns>
    private async Task<JsonDocument?> Get(string path, int limit, CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(http.Timeout == Timeout.InfiniteTimeSpan ? TimeSpan.FromSeconds(100) : http.Timeout);
        token = deadline.Token;
        using HttpResponseMessage response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length > limit)
        {
            throw Incomplete($"response for {path} exceeds {limit} bytes");
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(token);
        using MemoryStream buffered = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int count;
            while ((count = await body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - (int)buffered.Length + 1)), token)) != 0)
            {
                if (buffered.Length + count > limit)
                {
                    throw Incomplete($"response for {path} exceeds {limit} bytes");
                }

                buffered.Write(buffer, 0, count);
            }

            buffered.Position = 0;
            return await JsonDocument.ParseAsync(buffered, cancellationToken: token);
        }
        catch (JsonException)
        {
            throw Incomplete($"malformed JSON for {path}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value : throw Incomplete($"missing {name}");

    private static JsonElement.ArrayEnumerator Data(JsonDocument document)
    {
        JsonElement data = Property(document.RootElement, "data");
        return data.ValueKind == JsonValueKind.Array && data.GetArrayLength() <= MaxSlotBlobs
            ? data.EnumerateArray() : throw Incomplete($"blob data must be an array of at most {MaxSlotBlobs} blobs");
    }

    private static byte[] Blob(JsonElement element)
    {
        string? hex = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        if (hex is null || hex.Length != 2 + BlobStream.BytesPerBlob * 2 || !hex.StartsWith("0x", StringComparison.Ordinal))
        {
            throw Incomplete($"a blob must contain {BlobStream.BytesPerBlob} bytes");
        }

        try
        {
            return Bytes.FromHexString(hex);
        }
        catch (FormatException)
        {
            throw Incomplete("a blob contains invalid hex");
        }
    }

    private static ulong Quantity(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong number)
            ? number : throw Incomplete("invalid unsigned quantity");

    private static L1SourceIncompleteException Incomplete(string reason) => new(0, $"The beacon node returned {reason}.");

}

/// <summary>Matches blobs to the versioned hashes a transaction carries.</summary>
public static class BlobSelection
{
    private const int CommitmentSize = 48;

    /// <returns>The blob of each versioned hash, in order, or <see langword="null"/> when one is missing.</returns>
    public static byte[][]? Select(IReadOnlyList<byte[]> blobs, IReadOnlyList<Hash256> versionedHashes)
    {
        Dictionary<Hash256, byte[]> byHash = new(blobs.Count);
        foreach (byte[] blob in blobs)
        {
            if (blob.Length == BlobStream.BytesPerBlob)
            {
                byHash.TryAdd(VersionedHash(blob), blob);
            }
        }

        byte[][] selected = new byte[versionedHashes.Count][];
        for (int i = 0; i < selected.Length; i++)
        {
            if (!byHash.TryGetValue(versionedHashes[i], out byte[]? blob))
            {
                return null;
            }

            selected[i] = blob;
        }

        return selected;
    }

    public static Hash256 VersionedHash(ReadOnlySpan<byte> blob)
    {
        Span<byte> commitment = stackalloc byte[CommitmentSize];
        Span<byte> hash = stackalloc byte[Hash256.Size];
        KzgPolynomialCommitments.ComputeCommitment(blob, commitment);
        return KzgPolynomialCommitments.TryComputeCommitmentHashV1(commitment, hash)
            ? new Hash256(hash)
            : throw new InvalidOperationException("A KZG commitment does not hash.");
    }
}
