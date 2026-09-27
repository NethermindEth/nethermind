// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Evm;

/// <summary>
/// Per-transaction EIP-8279 meter of the bytes execution adds to the block access list, and the floor they extend.
/// </summary>
/// <remarks>
/// Every byte is counted before the matching block access list insertion, so an operation that cannot pay for its
/// bytes aborts first. The count is never rewound when a frame reverts, and only a storage slot restored to its
/// pre-transaction value gives its value bytes back, so it is an upper bound except for the give-backs that are not
/// journaled (see <see cref="TryMeterStorageValue"/>). Instances are reused across
/// transactions through <see cref="Reset"/>; not thread safe.
/// </remarks>
public sealed class BalDataMeter
{
    private readonly HashSet<StorageCell> _meteredStorageValues = new(StorageCell.EqualityComparer);
    private ulong _staticFloor;
    private ulong _floorLimit;

    /// <summary>Block access list bytes metered so far.</summary>
    public ulong BalDataBytes { get; private set; }

    /// <summary>The static floor extended by the metered bytes; the transaction's gas used cannot fall below it.</summary>
    public ulong FloorGas => _staticFloor + BalDataBytes * Eip8131Constants.FloorGasPerByte;

    /// <summary>Starts metering a transaction.</summary>
    /// <param name="staticFloor">The floor fixed before execution (EIP-8131 content floor plus the per-authorization term).</param>
    /// <param name="gasLimit">The transaction gas limit.</param>
    /// <returns>This meter.</returns>
    /// <remarks>
    /// The floor binds the execution dimension, which a transaction can use at most EIP-7825's cap of, so the
    /// extended floor is held to that cap as well as to the gas limit.
    /// </remarks>
    public BalDataMeter Reset(ulong staticFloor, ulong gasLimit)
    {
        _staticFloor = staticFloor;
        _floorLimit = Math.Min(gasLimit, Eip7825Constants.DefaultTxGasLimitCap);
        BalDataBytes = 0;
        _meteredStorageValues.Clear();
        return this;
    }

    /// <summary>Counts <paramref name="bytes"/> about to enter the block access list.</summary>
    /// <returns><see langword="false"/> (out of gas, nothing counted) when the extended floor would exceed the limit.</returns>
    public bool TryMeter(ulong bytes)
    {
        ulong balDataBytes = BalDataBytes + bytes;
        if (_staticFloor + balDataBytes * Eip8131Constants.FloorGasPerByte > _floorLimit) return false;
        BalDataBytes = balDataBytes;
        return true;
    }

    /// <summary>
    /// Keeps a slot's post-value bytes counted exactly while an SSTORE leaves it off its pre-transaction value.
    /// </summary>
    /// <param name="cell">The slot being written.</param>
    /// <param name="differsFromOriginal">Whether the value being written differs from the slot's pre-transaction value.</param>
    /// <returns><see langword="false"/> when counting the value bytes runs out of gas.</returns>
    /// <remarks>
    /// The block access list records one post value per changed slot and drops a slot restored to its original value
    /// to a read, so the value bytes are counted once on the first write away from the original and given back by a
    /// write that restores it. Tracking which slots are counted, rather than inferring it from the current value, keeps
    /// a slot dirtied in a reverted frame counted until a committed write restores it.
    /// Like the execution-specs reference, the set is not journaled: a give-back made in a frame that later reverts, or
    /// by an SSTORE that then runs out of gas on its own charge, stays given back even though the slot remains changed.
    /// </remarks>
    public bool TryMeterStorageValue(in StorageCell cell, bool differsFromOriginal)
    {
        if (differsFromOriginal)
        {
            if (_meteredStorageValues.Contains(cell)) return true;
            if (!TryMeter(Eip8279Constants.StorageValueBytes)) return false;
            _meteredStorageValues.Add(cell);
        }
        else if (_meteredStorageValues.Remove(cell))
        {
            BalDataBytes -= Eip8279Constants.StorageValueBytes;
        }

        return true;
    }
}
