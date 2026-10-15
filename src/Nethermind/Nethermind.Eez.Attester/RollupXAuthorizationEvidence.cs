// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Follower.RollupX;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Attester;

internal sealed class RollupXAuthorizationEvidence(SettlementRpcReader l1, SettlementRpcReader l2, Address registry, ulong rollupId)
{
    internal const int MaxLogs = 4096;
    internal const int MaxBatches = 128;
    private static readonly Hash256 RootTopic = Keccak.Compute("L2ExecutionPerformed(uint64,bytes32,uint256)");
    private static readonly Hash256 BatchTopic = Keccak.Compute("BatchPosted(bytes32,uint64[])");
    private static readonly Hash256 SkippedTopic = Keccak.Compute("L2TxSkipped(uint256,bytes)");
    private readonly Hash256 _rollupTopic = RollupTopic(rollupId);

    internal async Task CheckChains(ulong l1ChainId, ulong l2ChainId, CancellationToken cancellation)
    {
        if (SettlementRpcReader.Quantity(await l1.Read("eth_chainId", [], cancellation).ConfigureAwait(false)) != l1ChainId ||
            SettlementRpcReader.Quantity(await l2.Read("eth_chainId", [], cancellation).ConfigureAwait(false)) != l2ChainId)
            throw Rejected();
    }

    internal async Task<CanonicalL1Header> Reference(RollupXL1Reference trusted, CancellationToken cancellation)
    {
        CanonicalL1Header? finalized = null;
        if (trusted.Number != 0)
        {
            finalized = await Header("finalized", cancellation).ConfigureAwait(false);
            if (finalized.Number < trusted.Number) throw Rejected();
        }
        CanonicalL1Header reference = await HeaderAt(trusted.Number, cancellation).ConfigureAwait(false);
        if (reference.Hash != trusted.Hash || reference.Timestamp != trusted.Timestamp || reference.MixHash != trusted.PrevRandao ||
            (finalized is not null && (finalized.Timestamp < reference.Timestamp ||
                (finalized.Number == reference.Number && finalized.Hash != reference.Hash)))) throw Rejected();
        return reference;
    }

    internal async Task<CanonicalL1Header> HeaderAt(ulong number, CancellationToken cancellation)
    {
        CanonicalL1Header header = await Header(Quantity(number), cancellation).ConfigureAwait(false);
        return header.Number == number ? header : throw SettlementRpcReader.Unavailable();
    }

    internal async Task<BlockHeader> L2Header(Hash256 hash, CancellationToken cancellation)
    {
        byte[] bytes = SettlementRpcReader.Bytes(await l2.Read("debug_getRawHeader", [hash.ToString()], cancellation).ConfigureAwait(false));
        try
        {
            BlockHeader header = Rlp.Decode<BlockHeader>(bytes) ?? throw SettlementRpcReader.Unavailable();
            if (header.Hash != hash || header.StateRoot is null || header.MixHash is null ||
                !Rlp.Encode(header).Bytes.AsSpan().SequenceEqual(bytes)) throw SettlementRpcReader.Unavailable();
            return header;
        }
        catch (Exception exception) when (exception is RlpException or ArgumentException or IndexOutOfRangeException)
        {
            throw SettlementRpcReader.Unavailable();
        }
    }

    internal Task<CanonicalLog[]> Roots(ulong from, ulong to, CancellationToken cancellation) =>
        Logs(new
        {
            address = registry.ToString(),
            fromBlock = Quantity(from),
            toBlock = Quantity(to),
            topics = new[] { RootTopic.ToString(), _rollupTopic.ToString() }
        }, RootTopic, from, to, null, cancellation);

    internal async Task<Hash256[]> Endpoints(CanonicalL1Header block, CancellationToken cancellation)
    {
        CanonicalLog[] rootLogs = await BlockLogs(block, RootTopic, cancellation).ConfigureAwait(false);
        CanonicalLog[] batchLogs = await BlockLogs(block, BatchTopic, cancellation).ConfigureAwait(false);
        CanonicalLog[] skipLogs = await BlockLogs(block, SkippedTopic, cancellation).ConfigureAwait(false);
        ValidateIdentities(rootLogs, batchLogs, skipLogs);
        if (batchLogs.Length > MaxBatches) throw SettlementRpcReader.Unavailable();
        List<L1Batch> batches = new(batchLogs.Length);
        List<ClaimedChain> claims = new(batchLogs.Length);
        HashSet<ulong> transactions = new(batchLogs.Length);
        foreach (CanonicalLog log in batchLogs)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!transactions.Add(log.TransactionIndex)) throw SettlementRpcReader.Unavailable();
            PostBatch decoded = await Batch(block, log, cancellation).ConfigureAwait(false);
            ValidateBatchLog(log.Data, decoded);
            int updates = 0;
            if (decoded.Entries.Length > MaxLogs) throw SettlementRpcReader.Unavailable();
            foreach (ExecutionEntry entry in decoded.Entries)
            {
                if (entry.RollupUpdates.Length > MaxLogs - updates) throw SettlementRpcReader.Unavailable();
                updates += entry.RollupUpdates.Length;
            }
            HashSet<ulong> skipped = [];
            foreach (CanonicalLog skip in skipLogs)
            {
                if (skip.TransactionIndex != log.TransactionIndex) continue;
                if (skip.TransactionHash != log.TransactionHash || skip.IndexWord is not { } word ||
                    word.Bytes[..24].ContainsAnyExcept((byte)0)) throw SettlementRpcReader.Unavailable();
                ulong index = BinaryPrimitives.ReadUInt64BigEndian(word.Bytes[24..]);
                if (index >= (ulong)decoded.Entries.Length || !skipped.Add(index)) throw SettlementRpcReader.Unavailable();
            }
            batches.Add(L1Batch.Of(decoded, ReadOnlyMemory<byte>.Empty, rollupId, block.Number, block.Hash, log.TransactionHash, log.TransactionIndex));
            claims.Add(ClaimedChain.Of(decoded, rollupId, skipped));
        }
        SettledRoot[] roots = new SettledRoot[rootLogs.Length];
        for (int i = 0; i < roots.Length; i++)
        {
            CanonicalLog log = rootLogs[i];
            roots[i] = new SettledRoot(block.Number, block.Hash, log.TransactionIndex, log.LogIndex, new ValueHash256(log.Data.AsSpan(0, 32)));
        }
        L1Settlement[] settlements;
        try { settlements = RollupXSettlementAttribution.AttributeComplete(batches, claims, roots); }
        catch (EezSettlementException) { throw SettlementRpcReader.Unavailable(); }
        List<Hash256> endpoints = new(settlements.Length);
        foreach (L1Settlement settlement in settlements)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!settlement.IsEmpty) endpoints.Add(new Hash256(settlement.FinalState.Bytes));
        }
        if (rootLogs.Length != 0 && endpoints.Count == 0) throw SettlementRpcReader.Unavailable();
        return endpoints.ToArray();
    }

    private async Task<PostBatch> Batch(CanonicalL1Header block, CanonicalLog log, CancellationToken cancellation)
    {
        JsonElement transaction = await l1.Read("eth_getTransactionByBlockHashAndIndex", [block.Hash.ToString(), Quantity(log.TransactionIndex)],
            cancellation, SettlementRpcReader.BlockResponseLimit).ConfigureAwait(false);
        if (Hash(Field(transaction, "blockHash")) != block.Hash ||
            SettlementRpcReader.Quantity(Field(transaction, "blockNumber")) != block.Number ||
            SettlementRpcReader.Quantity(Field(transaction, "transactionIndex")) != log.TransactionIndex ||
            Hash(Field(transaction, "hash")) != log.TransactionHash) throw Stale();
        byte[] destination = SettlementRpcReader.Bytes(Field(transaction, "to"));
        if (!destination.AsSpan().SequenceEqual(registry.Bytes)) throw SettlementRpcReader.Unavailable();
        try { return EezCalldata.DecodePostAndVerifyBatch(SettlementRpcReader.Bytes(Field(transaction, "input"))); }
        catch (EezAbiException) { throw SettlementRpcReader.Unavailable(); }
    }

    private Task<CanonicalLog[]> BlockLogs(CanonicalL1Header block, Hash256 topic, CancellationToken cancellation) =>
        Logs(new
        {
            address = registry.ToString(),
            blockHash = block.Hash.ToString(),
            topics = topic == RootTopic ? new[] { topic.ToString(), _rollupTopic.ToString() } : [topic.ToString()]
        },
            topic, block.Number, block.Number, block.Hash, cancellation);

    private async Task<CanonicalLog[]> Logs(object filter, Hash256 topic, ulong from, ulong to, Hash256? hash, CancellationToken cancellation)
    {
        JsonElement array = await l1.Read("eth_getLogs", [filter], cancellation, SettlementRpcReader.BlockResponseLimit).ConfigureAwait(false);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxLogs) throw SettlementRpcReader.Unavailable();
        CanonicalLog[] logs = new CanonicalLog[array.GetArrayLength()];
        HashSet<(ulong Number, ulong Index)> positions = new(logs.Length);
        Dictionary<(ulong Number, ulong Transaction), Hash256> transactionHashes = new(logs.Length);
        int index = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();
            byte[] address = SettlementRpcReader.Bytes(Field(item, "address"));
            JsonElement removed = Field(item, "removed");
            if (!address.AsSpan().SequenceEqual(registry.Bytes) || removed.ValueKind != JsonValueKind.False) throw SettlementRpcReader.Unavailable();
            JsonElement topics = Field(item, "topics");
            int topicCount = topic == BatchTopic ? 1 : 2;
            if (topics.ValueKind != JsonValueKind.Array || topics.GetArrayLength() != topicCount || Hash(topics[0]) != topic ||
                (topic == RootTopic && Hash(topics[1]) != _rollupTopic)) throw SettlementRpcReader.Unavailable();
            ulong number = SettlementRpcReader.Quantity(Field(item, "blockNumber"));
            Hash256 blockHash = Hash(Field(item, "blockHash"));
            if (number < from || number > to || blockHash == Keccak.Zero) throw SettlementRpcReader.Unavailable();
            if (hash is not null && blockHash != hash) throw Stale();
            ulong transaction = SettlementRpcReader.Quantity(Field(item, "transactionIndex"));
            ulong logIndex = SettlementRpcReader.Quantity(Field(item, "logIndex"));
            Hash256 transactionHash = Hash(Field(item, "transactionHash"));
            byte[] data = SettlementRpcReader.Bytes(Field(item, "data"));
            if (transactionHash == Keccak.Zero || !positions.Add((number, logIndex)) ||
                (transactionHashes.TryGetValue((number, transaction), out Hash256? known) && known != transactionHash) ||
                (topic == RootTopic && data.Length != 64)) throw SettlementRpcReader.Unavailable();
            transactionHashes[(number, transaction)] = transactionHash;
            logs[index++] = new CanonicalLog(number, blockHash, transaction, transactionHash, logIndex, data,
                topic == SkippedTopic ? Hash(topics[1]) : null);
        }
        Array.Sort(logs, static (a, b) => a.Number != b.Number ? a.Number.CompareTo(b.Number) : a.LogIndex.CompareTo(b.LogIndex));
        for (int i = 1; i < logs.Length; i++)
        {
            if (logs[i].Number == logs[i - 1].Number && (logs[i].BlockHash != logs[i - 1].BlockHash ||
                logs[i].TransactionIndex < logs[i - 1].TransactionIndex)) throw SettlementRpcReader.Unavailable();
        }
        return logs;
    }

    private static void ValidateIdentities(CanonicalLog[] roots, CanonicalLog[] batches, CanonicalLog[] skips)
    {
        HashSet<ulong> positions = new(roots.Length + batches.Length + skips.Length);
        Dictionary<ulong, Hash256> transactions = [];
        foreach (CanonicalLog[] category in new[] { roots, batches, skips })
        {
            foreach (CanonicalLog log in category)
            {
                if (!positions.Add(log.LogIndex) || (transactions.TryGetValue(log.TransactionIndex, out Hash256? known) && known != log.TransactionHash))
                    throw SettlementRpcReader.Unavailable();
                transactions[log.TransactionIndex] = log.TransactionHash;
            }
        }
    }

    private static void ValidateBatchLog(byte[] data, PostBatch batch)
    {
        if (data.Length < 96 || data.AsSpan(32, 31).ContainsAnyExcept((byte)0) || data[63] != 64 ||
            data.AsSpan(64, 24).ContainsAnyExcept((byte)0)) throw SettlementRpcReader.Unavailable();
        ulong count = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(88, 8));
        if (count != (ulong)batch.RollupIdsWithProofSystems.Length || count > MaxBatches || data.Length != 96 + 32 * (int)count)
            throw SettlementRpcReader.Unavailable();
        for (int i = 0; i < (int)count; i++)
        {
            ReadOnlySpan<byte> word = data.AsSpan(96 + 32 * i, 32);
            if (word[..24].ContainsAnyExcept((byte)0) || BinaryPrimitives.ReadUInt64BigEndian(word[24..]) != batch.RollupIdsWithProofSystems[i].RollupId)
                throw SettlementRpcReader.Unavailable();
        }
    }

    internal static JsonElement Field(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) throw SettlementRpcReader.Unavailable();
        JsonElement result = default;
        int count = 0;
        foreach (JsonProperty property in value.EnumerateObject())
            if (property.NameEquals(name)) { result = property.Value; count++; }
        return count == 1 ? result : throw SettlementRpcReader.Unavailable();
    }

    internal static Hash256 Hash(JsonElement value)
    {
        byte[] bytes = SettlementRpcReader.Bytes(value);
        return bytes.Length == 32 ? new Hash256(bytes) : throw SettlementRpcReader.Unavailable();
    }

    private async Task<CanonicalL1Header> Header(string selector, CancellationToken cancellation)
    {
        JsonElement block = await l1.Read("eth_getBlockByNumber", [selector, false], cancellation, SettlementRpcReader.BlockResponseLimit).ConfigureAwait(false);
        ulong number = SettlementRpcReader.Quantity(Field(block, "number"));
        Hash256 hash = Hash(Field(block, "hash"));
        if (hash == Keccak.Zero) throw SettlementRpcReader.Unavailable();
        return new CanonicalL1Header(number, hash, SettlementRpcReader.Quantity(Field(block, "timestamp")), Hash(Field(block, "mixHash")));
    }

    private static Hash256 RollupTopic(ulong id)
    {
        byte[] word = new byte[32];
        BinaryPrimitives.WriteUInt64BigEndian(word.AsSpan(24), id);
        return new Hash256(word);
    }

    internal static string Quantity(ulong number) => "0x" + number.ToString("x", CultureInfo.InvariantCulture);
    internal static RpcException Rejected() => new(new Status(StatusCode.FailedPrecondition, "settlement authorization rejected"));
    internal static RpcException Stale() => new(new Status(StatusCode.Aborted, "settlement target no longer canonical"));
}

internal sealed record CanonicalL1Header(ulong Number, Hash256 Hash, ulong Timestamp, Hash256 MixHash)
{
    internal L1AuthorizationSnapshot Snapshot => new(Number, Hash);
}

internal sealed record CanonicalLog(ulong Number, Hash256 BlockHash, ulong TransactionIndex, Hash256 TransactionHash,
    ulong LogIndex, byte[] Data, Hash256? IndexWord);
