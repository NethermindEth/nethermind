// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

public enum FeeRecipientKind : byte
{
    None = 0,
    GasBeneficiary = 1,
    FeeCollector = 2
}

public enum ParallelStateKeyKind : byte
{
    Storage = 0,
    Account = 1,
    StorageClear = 2,
    FeeGasBeneficiary = 3,
    FeeCollector = 4,
}

/// <summary>
/// Tagged key for the multi-version memory. Discriminates between per-account state, per-slot
/// storage, the selfdestruct/clear marker, and the per-tx fee buckets.
/// </summary>
/// <remarks>
/// <para>The original implementation overloaded a "<see cref="StorageCell"/> with a magic slot
/// index" to encode both the account-level key and the storage-clear sentinel. The former
/// relied on a single-arg <c>new StorageCell(addr)</c> ctor that no longer exists on master,
/// and the latter aliased <see cref="Nethermind.Core.Crypto.Keccak.EmptyTreeHash"/> as a slot
/// index — colliding with any real <c>SSTORE</c> at that index (audit bug B2). The current
/// shape uses explicit kinds, keyed by <see cref="Address"/> for account / clear and by
/// <see cref="StorageCell"/> for slot; fees remain keyed by <c>(kind, txIndex)</c>.</para>
/// </remarks>
public readonly struct ParallelStateKey : IEquatable<ParallelStateKey>
{
    private readonly StorageCell _storageCell;
    private readonly Address? _address;
    private readonly int _txIndex;
    private readonly ParallelStateKeyKind _kind;

    private ParallelStateKey(StorageCell storageCell)
    {
        _storageCell = storageCell;
        _address = null;
        _txIndex = 0;
        _kind = ParallelStateKeyKind.Storage;
    }

    private ParallelStateKey(Address address, ParallelStateKeyKind kind)
    {
        _storageCell = default;
        _address = address;
        _txIndex = 0;
        _kind = kind;
    }

    private ParallelStateKey(ParallelStateKeyKind kind, int txIndex)
    {
        _storageCell = default;
        _address = null;
        _txIndex = txIndex;
        _kind = kind;
    }

    public ParallelStateKeyKind Kind => _kind;

    public StorageCell StorageCell => _storageCell;

    public Address Address => _address ?? _storageCell.Address;

    public int TxIndex => _txIndex;

    public static ParallelStateKey ForStorage(StorageCell storageCell) => new(storageCell);

    public static ParallelStateKey ForAccount(Address address) => new(address, ParallelStateKeyKind.Account);

    public static ParallelStateKey ForStorageClear(Address address) => new(address, ParallelStateKeyKind.StorageClear);

    public static ParallelStateKey ForFee(FeeRecipientKind kind, int txIndex) =>
        new(kind == FeeRecipientKind.GasBeneficiary ? ParallelStateKeyKind.FeeGasBeneficiary : ParallelStateKeyKind.FeeCollector, txIndex);

    public bool Equals(ParallelStateKey other)
    {
        if (_kind != other._kind) return false;
        return _kind switch
        {
            ParallelStateKeyKind.Storage => _storageCell.Equals(other._storageCell),
            ParallelStateKeyKind.Account or ParallelStateKeyKind.StorageClear => _address! == other._address!,
            _ => _txIndex == other._txIndex,
        };
    }

    public override bool Equals(object? obj) => obj is ParallelStateKey other && Equals(other);

    public override int GetHashCode() => _kind switch
    {
        ParallelStateKeyKind.Storage => HashCode.Combine(_kind, _storageCell),
        ParallelStateKeyKind.Account or ParallelStateKeyKind.StorageClear => HashCode.Combine(_kind, _address),
        _ => HashCode.Combine(_kind, _txIndex),
    };

    public override string ToString() => _kind switch
    {
        ParallelStateKeyKind.Storage => _storageCell.ToString(),
        ParallelStateKeyKind.Account => $"Account:{_address}",
        ParallelStateKeyKind.StorageClear => $"Clear:{_address}",
        _ => $"{_kind}:{_txIndex}",
    };
}
