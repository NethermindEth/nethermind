// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Evm;

/// <summary>
/// Per-transaction EIP-8279 meter of the bytes execution adds to the block access list, and the floor they extend.
/// </summary>
/// <remarks>
/// Every byte is counted before the matching block access list insertion, so an operation that cannot pay for its
/// bytes aborts first. The count is an upper bound: nothing is ever given back, and neither the count nor the sets of
/// metered accounts and slots is rewound when a frame reverts, since the block access list keeps what a reverted frame
/// touched. Instances are reused across transactions through <see cref="Reset"/>; not thread safe.
/// </remarks>
public sealed class BalDataMeter
{
    private readonly HashSet<AddressAsKey> _meteredAddresses = [with(AddressAsKey.EqualityComparer)];
    private readonly HashSet<StorageCell> _meteredStorageKeys = [with(StorageCell.EqualityComparer)];
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
        // Trimmed after a large transaction so later resets do not keep walking its buckets.
        _meteredAddresses.ClearAndTrim();
        _meteredStorageKeys.ClearAndTrim();
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

    /// <summary>Meters an account's address bytes on the transaction's first access to it.</summary>
    /// <returns><see langword="false"/> when metering the address runs out of gas.</returns>
    /// <remarks>
    /// First access is tracked per transaction, not by the frame's warm set: the block access list keeps an account
    /// touched by a frame that later reverts, and an account pre-warmed by the access list, the transaction itself or
    /// as a precompile still enters it when first touched.
    /// </remarks>
    public bool TryMeterAddress(Address address)
    {
        if (!_meteredAddresses.Add(address)) return true;
        if (TryMeter(Eip8279Constants.AddressBytes)) return true;
        _meteredAddresses.Remove(address);
        return false;
    }

    /// <summary>Meters a storage slot's key bytes on the transaction's first access to it.</summary>
    /// <returns><see langword="false"/> when metering the key runs out of gas.</returns>
    /// <remarks>Tracked per transaction like <see cref="TryMeterAddress"/>.</remarks>
    public bool TryMeterStorageKey(in StorageCell cell)
    {
        if (!_meteredStorageKeys.Add(cell)) return true;
        if (TryMeter(Eip8279Constants.StorageKeyBytes)) return true;
        _meteredStorageKeys.Remove(cell);
        return false;
    }
}
