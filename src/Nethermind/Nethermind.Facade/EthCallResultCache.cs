// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade;

/// <summary>
/// Block-scoped memoization of eth_call results (experimental, NETHERMIND_ETHCALL_CACHE=1).
///
/// Correctness rests on EVM determinism: a call against a specific BLOCK HASH — which pins the
/// state root AND every block-environment opcode (NUMBER, TIMESTAMP, COINBASE, PREVRANDAO,
/// BASEFEE, blob fees) — with identical (from, to, value, gas, fee fields, calldata) produces
/// an identical result, byte for byte. There is no staleness window and no invalidation logic:
/// a new head is a new block hash, hence a different key. Entries simply age out of the LRU.
///
/// Deliberately NOT cached: state/block overrides (the exclusive path never consults this
/// cache), access-list and blob transactions, contract creations (result depends on nonce),
/// and oversized outputs (memory bound). A cancelled call throws before Set, so timeouts are
/// never cached.
/// </summary>
public static class EthCallResultCache
{
    private const int Capacity = 1024;
    private const int MaxCachedOutputBytes = 1024 * 1024;

    // Volatile: set at startup from the environment, flipped at runtime only by tests.
    public static volatile bool Enabled = Environment.GetEnvironmentVariable("NETHERMIND_ETHCALL_CACHE") == "1";

    private static readonly LruCache<ValueHash256, CallOutput> s_cache = new(Capacity, "eth_call results");

    // Observability (lossy adds; dashboards and test assertions).
    public static long Hits;
    public static long Misses;

    /// <summary>
    /// Computes the cache key when the call is cacheable. The key is the Keccak of every
    /// input that can influence the result; anything outside this set must stay uncacheable.
    /// </summary>
    public static bool TryComputeKey(BlockHeader header, Transaction tx, out ValueHash256 key)
    {
        key = default;
        if (header.Hash is null
            || tx.To is null
            || tx.AccessList is not null
            || tx.BlobVersionedHashes is not null)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[32 + 20 + 20 + 32 + 8 + 32 + 32 + 32 + 32];
        int offset = 0;

        header.Hash.Bytes.CopyTo(buffer);
        offset += 32;
        (tx.SenderAddress ?? Address.Zero).Bytes.CopyTo(buffer[offset..]);
        offset += 20;
        tx.To.Bytes.CopyTo(buffer[offset..]);
        offset += 20;
        WriteUInt256(buffer, ref offset, tx.Value);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(buffer[offset..], tx.GasLimit);
        offset += 8;
        WriteUInt256(buffer, ref offset, tx.GasPrice);
        WriteUInt256(buffer, ref offset, tx.DecodedMaxFeePerGas);
        WriteUInt256(buffer, ref offset, tx.MaxPriorityFeePerGas);
        ValueKeccak.Compute(tx.Data.Span).Bytes.CopyTo(buffer[offset..]);
        offset += 32;

        key = ValueKeccak.Compute(buffer[..offset]);
        return true;
    }

    public static bool TryGet(in ValueHash256 key, out CallOutput output)
    {
        if (s_cache.TryGet(key, out output!))
        {
            Hits++;
            return true;
        }

        Misses++;
        return false;
    }

    public static void Set(in ValueHash256 key, CallOutput output)
    {
        if (output.OutputData is { Length: > MaxCachedOutputBytes })
            return;
        s_cache.Set(key, output);
    }

    private static void WriteUInt256(Span<byte> buffer, ref int offset, in UInt256 value)
    {
        value.ToBigEndian(buffer.Slice(offset, 32));
        offset += 32;
    }
}
