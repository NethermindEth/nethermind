// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>Bounded, thread-safe memoization of RPC responses for identical requests executed against the same block.</summary>
/// <typeparam name="T">The result type wrapped by the cached <see cref="ResultWrapper{T}"/>.</typeparam>
/// <remarks>
/// Keys embed the resolved block hash, so a stale entry can never be served for a new head and no explicit
/// invalidation is needed — entries for old blocks simply age out of the bounded cache. Only deterministic
/// outcomes (successful execution and reverts) are stored; infrastructure failures such as timeouts or
/// unavailable state are not. Requests carrying state or block overrides must bypass the cache entirely.
/// </remarks>
public sealed class EthResponseCache<T>(int maxCapacity, Action reportHit, Action reportMiss)
{
    private readonly ClockCache<ValueHash256, ResultWrapper<T>> _cache = new(maxCapacity);

    /// <summary>Tries to retrieve a previously memoized response, updating hit/miss metrics.</summary>
    public bool TryGet(in ValueHash256 key, [NotNullWhen(true)] out ResultWrapper<T>? result)
    {
        if (_cache.TryGet(key, out result!))
        {
            reportHit();
            return true;
        }

        reportMiss();
        return false;
    }

    /// <summary>Stores the response if its outcome is deterministic: successful execution or a revert.</summary>
    public void SetIfCacheable(in ValueHash256 key, ResultWrapper<T> result)
    {
        if (result.Result.ResultType == ResultType.Success || result.ErrorCode == ErrorCodes.ExecutionReverted)
        {
            _cache.Set(key, result);
        }
    }
}

/// <summary>Factories and key derivation for the per-block RPC response caches.</summary>
public static class EthResponseCache
{
    /// <summary>Creates the <c>eth_call</c> cache sized by <see cref="IJsonRpcConfig.EthCallCacheSize"/>, or <c>null</c> when caching is disabled.</summary>
    public static EthResponseCache<HexBytes>? CreateCallCacheIfEnabled(IJsonRpcConfig config) =>
        config.EthCallCacheSize > 0
            ? new(config.EthCallCacheSize,
                static () => Interlocked.Increment(ref Metrics.EthCallCacheHits),
                static () => Interlocked.Increment(ref Metrics.EthCallCacheMisses))
            : null;

    /// <summary>Creates the <c>eth_getBalance</c> cache sized by <see cref="IJsonRpcConfig.EthCallCacheSize"/>, or <c>null</c> when caching is disabled.</summary>
    public static EthResponseCache<UInt256?>? CreateBalanceCacheIfEnabled(IJsonRpcConfig config) =>
        config.EthCallCacheSize > 0
            ? new(config.EthCallCacheSize,
                static () => Interlocked.Increment(ref Metrics.EthBalanceCacheHits),
                static () => Interlocked.Increment(ref Metrics.EthBalanceCacheMisses))
            : null;

    /// <summary>Computes the cache key for a call executed against the given resolved block.</summary>
    /// <remarks>
    /// The key is a single keccak over the block hash, a runtime-type marker, and an injective field-wise
    /// encoding of the call: nullable fixed-width fields carry a presence byte and variable-length fields a
    /// length prefix, so the byte stream is decodable and two calls share a key only when every
    /// execution-relevant field is identical. A runtime type other than the built-in signable transaction
    /// types (e.g. registered by a plugin) may carry fields this encoding does not know about, so it falls
    /// back to hashing the call's polymorphic JSON form, which serializes every field of the concrete type.
    /// </remarks>
    public static ValueHash256 ComputeCallKey(Hash256 blockHash, TransactionForRpc call)
    {
        Type callType = call.GetType();
        byte typeMarker;
        if (callType == typeof(LegacyTransactionForRpc)) typeMarker = 0;
        else if (callType == typeof(AccessListTransactionForRpc)) typeMarker = 1;
        else if (callType == typeof(EIP1559TransactionForRpc)) typeMarker = 2;
        else if (callType == typeof(BlobTransactionForRpc)) typeMarker = 3;
        else if (callType == typeof(SetCodeTransactionForRpc)) typeMarker = 4;
        else return ComputeJsonCallKey(blockHash, call);

        KeccakHash keccak = KeccakHash.Create();
        keccak.Update(blockHash.Bytes);
        UpdateByte(keccak, typeMarker);

        LegacyTransactionForRpc legacy = (LegacyTransactionForRpc)call;
        Update(keccak, legacy.Nonce);
        Update(keccak, legacy.Gas);
        Update(keccak, legacy.ChainId);
        Update(keccak, legacy.To);
        Update(keccak, legacy.From);
        Update(keccak, legacy.Value);
        Update(keccak, legacy.GasPrice);
        Update(keccak, legacy.V);
        Update(keccak, legacy.R);
        Update(keccak, legacy.S);
        Update(keccak, legacy.Input);

        if (call is AccessListTransactionForRpc accessListTx)
        {
            Update(keccak, accessListTx.YParity);
            Update(keccak, accessListTx.AccessList);
        }

        if (call is EIP1559TransactionForRpc eip1559)
        {
            Update(keccak, eip1559.MaxPriorityFeePerGas);
            Update(keccak, eip1559.MaxFeePerGas);
        }

        if (call is BlobTransactionForRpc blob)
        {
            Update(keccak, blob.MaxFeePerBlobGas);
            Update(keccak, blob.BlobVersionedHashes);
            Update(keccak, blob.Blobs);
            Update(keccak, blob.Commitments);
            Update(keccak, blob.Proofs);
        }

        if (call is SetCodeTransactionForRpc setCode)
        {
            Update(keccak, setCode.AuthorizationList);
        }

        return keccak.GenerateValueHash();
    }

    private static ValueHash256 ComputeJsonCallKey(Hash256 blockHash, TransactionForRpc call)
    {
        byte[] callJson = JsonSerializer.SerializeToUtf8Bytes(call, EthereumJsonSerializer.JsonOptions);
        ValueHash256 callHash = ValueKeccak.Compute(callJson);
        Span<byte> keyMaterial = stackalloc byte[2 * Hash256.Size];
        blockHash.Bytes.CopyTo(keyMaterial);
        callHash.Bytes.CopyTo(keyMaterial[Hash256.Size..]);
        return ValueKeccak.Compute(keyMaterial);
    }

    private const byte Absent = 0;
    private const byte Present = 1;
    private const int WordSize = 32;

    private static void UpdateByte(KeccakHash keccak, byte value)
    {
        ReadOnlySpan<byte> span = [value];
        keccak.Update(span);
    }

    private static void Update(KeccakHash keccak, ulong? value)
    {
        if (value is null)
        {
            UpdateByte(keccak, Absent);
            return;
        }

        Span<byte> buffer = stackalloc byte[1 + sizeof(ulong)];
        buffer[0] = Present;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[1..], value.Value);
        keccak.Update(buffer);
    }

    private static void Update(KeccakHash keccak, in UInt256? value)
    {
        if (value is null)
        {
            UpdateByte(keccak, Absent);
            return;
        }

        Span<byte> buffer = stackalloc byte[1 + WordSize];
        buffer[0] = Present;
        value.Value.ToBigEndian(buffer[1..]);
        keccak.Update(buffer);
    }

    private static void Update(KeccakHash keccak, Address? value)
    {
        if (value is null)
        {
            UpdateByte(keccak, Absent);
            return;
        }

        Span<byte> buffer = stackalloc byte[1 + Address.Size];
        buffer[0] = Present;
        value.Bytes.CopyTo(buffer[1..]);
        keccak.Update(buffer);
    }

    private static void Update(KeccakHash keccak, byte[]? value)
    {
        if (value is null)
        {
            UpdateByte(keccak, Absent);
            return;
        }

        Span<byte> prefix = stackalloc byte[1 + sizeof(int)];
        prefix[0] = Present;
        BinaryPrimitives.WriteInt32LittleEndian(prefix[1..], value.Length);
        keccak.Update(prefix);
        keccak.Update(value);
    }

    private static void Update(KeccakHash keccak, byte[]?[]? value)
    {
        if (value is null)
        {
            UpdateByte(keccak, Absent);
            return;
        }

        Span<byte> prefix = stackalloc byte[1 + sizeof(int)];
        prefix[0] = Present;
        BinaryPrimitives.WriteInt32LittleEndian(prefix[1..], value.Length);
        keccak.Update(prefix);
        foreach (byte[]? element in value)
        {
            Update(keccak, element);
        }
    }

    private static void Update(KeccakHash keccak, AccessListForRpc? value)
    {
        if (value is null)
        {
            UpdateByte(keccak, Absent);
            return;
        }

        // Hash the converted form the executor consumes (ToAccessList), so the key is exactly as
        // fine-grained as the execution input.
        AccessList accessList = value.ToAccessList();
        Span<byte> prefix = stackalloc byte[1 + sizeof(int)];
        prefix[0] = Present;
        BinaryPrimitives.WriteInt32LittleEndian(prefix[1..], accessList.Count.AddressesCount);
        keccak.Update(prefix);

        Span<byte> entry = stackalloc byte[Address.Size + sizeof(int)];
        Span<byte> storageKeyBytes = stackalloc byte[WordSize];
        foreach ((Address address, AccessList.StorageKeysEnumerable storageKeys) in accessList)
        {
            address.Bytes.CopyTo(entry);
            BinaryPrimitives.WriteInt32LittleEndian(entry[Address.Size..], storageKeys.Count);
            keccak.Update(entry);
            foreach (UInt256 storageKey in storageKeys)
            {
                storageKey.ToBigEndian(storageKeyBytes);
                keccak.Update(storageKeyBytes);
            }
        }
    }

    private static void Update(KeccakHash keccak, AuthorizationListForRpc? value)
    {
        if (value is null)
        {
            UpdateByte(keccak, Absent);
            return;
        }

        UpdateByte(keccak, Present);
        foreach (AuthorizationListForRpc.RpcAuthTuple tuple in value)
        {
            // A per-item marker plus a final Absent keeps the stream self-delimiting without a count.
            UpdateByte(keccak, Present);
            Update(keccak, (UInt256?)tuple.ChainId);
            Update(keccak, (ulong?)tuple.Nonce);
            Update(keccak, tuple.Address);
            Update(keccak, tuple.YParity);
            Update(keccak, (UInt256?)tuple.R);
            Update(keccak, (UInt256?)tuple.S);
        }

        UpdateByte(keccak, Absent);
    }

    /// <summary>Computes the cache key for a balance query executed against the given resolved block.</summary>
    public static ValueHash256 ComputeBalanceKey(Hash256 blockHash, Address address)
    {
        Span<byte> keyMaterial = stackalloc byte[Hash256.Size + Address.Size];
        blockHash.Bytes.CopyTo(keyMaterial);
        address.Bytes.CopyTo(keyMaterial[Hash256.Size..]);
        return ValueKeccak.Compute(keyMaterial);
    }
}
