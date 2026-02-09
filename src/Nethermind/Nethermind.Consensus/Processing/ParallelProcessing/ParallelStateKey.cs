// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public enum FeeRecipientKind : byte
{
    GasBeneficiary = 0,
    FeeCollector = 1
}

public enum ParallelStateKeyKind : byte
{
    Storage = 0,
    FeeGasBeneficiary = 1,
    FeeCollector = 2
}

public readonly struct ParallelStateKey : IEquatable<ParallelStateKey>
{
    private readonly StorageCell _storageCell;
    private readonly int _txIndex;
    private readonly ParallelStateKeyKind _kind;

    private ParallelStateKey(StorageCell storageCell)
    {
        _storageCell = storageCell;
        _txIndex = 0;
        _kind = ParallelStateKeyKind.Storage;
    }

    private ParallelStateKey(ParallelStateKeyKind kind, int txIndex)
    {
        _storageCell = default;
        _txIndex = txIndex;
        _kind = kind;
    }

    public ParallelStateKeyKind Kind => _kind;

    public StorageCell StorageCell => _storageCell;

    public static ParallelStateKey ForStorage(StorageCell storageCell) => new(storageCell);

    public static ParallelStateKey ForFee(FeeRecipientKind kind, int txIndex) =>
        new(kind == FeeRecipientKind.GasBeneficiary ? ParallelStateKeyKind.FeeGasBeneficiary : ParallelStateKeyKind.FeeCollector, txIndex);

    public bool Equals(ParallelStateKey other) =>
        _kind == other._kind && (_kind == ParallelStateKeyKind.Storage ? _storageCell.Equals(other._storageCell) : _txIndex == other._txIndex);

    public override bool Equals(object? obj) => obj is ParallelStateKey other && Equals(other);

    public override int GetHashCode() =>
        _kind == ParallelStateKeyKind.Storage
            ? HashCode.Combine(_kind, _storageCell)
            : HashCode.Combine(_kind, _txIndex);

    public override string ToString() =>
        _kind == ParallelStateKeyKind.Storage ? _storageCell.ToString() : $"{_kind}:{_txIndex}";
}
