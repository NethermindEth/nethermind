// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;

namespace Nethermind.StatelessInputGen;

/// <summary>Reads a block's EIP-7685 execution requests from a beacon node.</summary>
/// <remarks>
/// The beacon block body carries the requests the execution block committed to, so fetching them is far cheaper
/// than recovering them by replaying the block. The result is only used when it hashes to the header's
/// <c>requestsHash</c>, so a wrong or lagging beacon node can't change the generated input.
/// </remarks>
internal static class BeaconRequests
{
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <returns>The flat-encoded requests, or <c>null</c> with the reason if they can't be used.</returns>
    internal static async Task<(byte[][]? Requests, string? Error)> TryFetch(Uri beaconUrl, Block block, CancellationToken cancellationToken)
    {
        using JsonDocument genesis = await Get(beaconUrl, "eth/v1/beacon/genesis", cancellationToken);
        using JsonDocument spec = await Get(beaconUrl, "eth/v1/config/spec", cancellationToken);
        ulong genesisTime = ulong.Parse(genesis.RootElement.GetProperty("data").GetProperty("genesis_time").GetString()!, CultureInfo.InvariantCulture);
        ulong secondsPerSlot = ulong.Parse(spec.RootElement.GetProperty("data").GetProperty("SECONDS_PER_SLOT").GetString()!, CultureInfo.InvariantCulture);

        if (block.Timestamp < genesisTime || (block.Timestamp - genesisTime) % secondsPerSlot != 0)
            return (null, "the block timestamp doesn't fall on a beacon slot");

        ulong slot = (block.Timestamp - genesisTime) / secondsPerSlot;
        using JsonDocument beaconBlock = await Get(beaconUrl, $"eth/v2/beacon/blocks/{slot}", cancellationToken);
        JsonElement body = beaconBlock.RootElement.GetProperty("data").GetProperty("message").GetProperty("body");

        string? blockHash = body.GetProperty("execution_payload").GetProperty("block_hash").GetString();
        if (!string.Equals(blockHash, block.Hash?.ToString(), StringComparison.OrdinalIgnoreCase))
            return (null, $"beacon slot {slot} holds execution block {blockHash}, not {block.Hash}");

        if (!body.TryGetProperty("execution_requests", out JsonElement requests))
            return (null, $"beacon block at slot {slot} has no execution requests");

        List<byte[]> flat = [];
        foreach (JsonProperty group in requests.EnumerateObject())
        {
            (ExecutionRequestType Type, Func<JsonElement, byte[]> Encode)? codec = group.Name switch
            {
                "deposits" => (ExecutionRequestType.Deposit, EncodeDeposit),
                "withdrawals" => (ExecutionRequestType.WithdrawalRequest, EncodeWithdrawal),
                "consolidations" => (ExecutionRequestType.ConsolidationRequest, EncodeConsolidation),
                _ => null
            };

            if (codec is not { } c)
                return (null, $"unknown execution request group `{group.Name}`");

            if (group.Value.GetArrayLength() == 0)
                continue;

            List<byte> encoded = [(byte)c.Type];
            foreach (JsonElement request in group.Value.EnumerateArray())
                encoded.AddRange(c.Encode(request));

            flat.Add([.. encoded]);
        }

        // Groups must be in type order for the hash; the beacon API doesn't promise an order.
        byte[][] result = [.. flat.OrderBy(static r => r[0])];
        Hash256 hash = ExecutionRequestExtensions.CalculateHashFromFlatEncodedRequests(result);

        return hash == block.Header.RequestsHash
            ? (result, null)
            : (null, $"beacon requests hash to {hash}, but the header has {block.Header.RequestsHash}");
    }

    private static byte[] EncodeDeposit(JsonElement deposit) =>
    [
        .. Bytes(deposit, "pubkey", ExecutionRequestExtensions.PublicKeySize),
        .. Bytes(deposit, "withdrawal_credentials", ExecutionRequestExtensions.WithdrawalCredentialsSize),
        .. UInt64(deposit, "amount"),
        .. Bytes(deposit, "signature", ExecutionRequestExtensions.SignatureSize),
        .. UInt64(deposit, "index")
    ];

    private static byte[] EncodeWithdrawal(JsonElement withdrawal) =>
    [
        .. Bytes(withdrawal, "source_address", Address.Size),
        .. Bytes(withdrawal, "validator_pubkey", ExecutionRequestExtensions.PublicKeySize),
        .. UInt64(withdrawal, "amount")
    ];

    private static byte[] EncodeConsolidation(JsonElement consolidation) =>
    [
        .. Bytes(consolidation, "source_address", Address.Size),
        .. Bytes(consolidation, "source_pubkey", ExecutionRequestExtensions.PublicKeySize),
        .. Bytes(consolidation, "target_pubkey", ExecutionRequestExtensions.PublicKeySize)
    ];

    private static byte[] Bytes(JsonElement element, string name, int size)
    {
        string hex = element.GetProperty(name).GetString()!;
        byte[] bytes = Convert.FromHexString(hex.AsSpan(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2 : 0));

        return bytes.Length == size
            ? bytes
            : throw new InvalidDataException($"Beacon request field `{name}` is {bytes.Length} bytes, expected {size}");
    }

    // SSZ uint64 is little-endian; the beacon API sends it as a decimal string.
    private static byte[] UInt64(JsonElement element, string name)
    {
        byte[] bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, ulong.Parse(element.GetProperty(name).GetString()!, CultureInfo.InvariantCulture));
        return bytes;
    }

    private static async Task<JsonDocument> Get(Uri beaconUrl, string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(new Uri(beaconUrl, path), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
