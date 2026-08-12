// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>Bounded, thread-safe memoization of <c>eth_call</c> responses for identical calls executed against the same block.</summary>
/// <remarks>
/// Keys embed the resolved block hash, so a stale entry can never be served for a new head and no explicit
/// invalidation is needed — entries for old blocks simply age out of the bounded cache. Only deterministic
/// outcomes (successful execution and reverts) are stored; infrastructure failures such as timeouts or
/// unavailable state are not. Calls carrying state or block overrides must bypass the cache entirely.
/// </remarks>
public sealed class EthCallResponseCache(int maxCapacity)
{
    private readonly ClockCache<ValueHash256, ResultWrapper<HexBytes>> _cache = new(maxCapacity);

    /// <summary>Creates a cache sized by <see cref="IJsonRpcConfig.EthCallCacheSize"/>, or <c>null</c> when the cache is disabled.</summary>
    public static EthCallResponseCache? CreateIfEnabled(IJsonRpcConfig config) =>
        config.EthCallCacheSize > 0 ? new EthCallResponseCache(config.EthCallCacheSize) : null;

    /// <summary>Computes the cache key for a call executed against the given resolved block.</summary>
    /// <remarks>
    /// The key is <c>keccak(blockHash ++ keccak(callJson))</c> where <c>callJson</c> is the polymorphic JSON
    /// form of the call. Serializing via the runtime transaction type captures every execution-relevant field
    /// of the concrete type (to, from, value, gas, gas price / max fee fields, data, access list, ...), so two
    /// calls share a key only when their full parameter sets are identical.
    /// </remarks>
    public static ValueHash256 ComputeKey(Hash256 blockHash, TransactionForRpc call)
    {
        byte[] callJson = JsonSerializer.SerializeToUtf8Bytes(call, EthereumJsonSerializer.JsonOptions);
        ValueHash256 callHash = ValueKeccak.Compute(callJson);
        Span<byte> keyMaterial = stackalloc byte[2 * Hash256.Size];
        blockHash.Bytes.CopyTo(keyMaterial);
        callHash.Bytes.CopyTo(keyMaterial[Hash256.Size..]);
        return ValueKeccak.Compute(keyMaterial);
    }

    /// <summary>Tries to retrieve a previously memoized response, updating hit/miss metrics.</summary>
    public bool TryGet(in ValueHash256 key, [NotNullWhen(true)] out ResultWrapper<HexBytes>? result)
    {
        if (_cache.TryGet(key, out result!))
        {
            Interlocked.Increment(ref Metrics.EthCallCacheHits);
            return true;
        }

        Interlocked.Increment(ref Metrics.EthCallCacheMisses);
        return false;
    }

    /// <summary>Stores the response if its outcome is deterministic: successful execution or a revert.</summary>
    public void SetIfCacheable(in ValueHash256 key, ResultWrapper<HexBytes> result)
    {
        if (result.Result.ResultType == ResultType.Success || result.ErrorCode == ErrorCodes.ExecutionReverted)
        {
            _cache.Set(key, result);
        }
    }
}
