// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm;

/// <summary>
/// EIP-8279 per-transaction meter of bytes the transaction contributes to the EIP-7928 block
/// access list. The floor grows as new BAL bytes are metered and the transaction runs out of
/// gas the moment the floor exceeds its gas limit.
/// </summary>
/// <remarks>
/// Metered bytes are never rolled back on frame reverts — reverted frames keep their BAL
/// accesses under EIP-7928 — except for the explicit storage-value refund when a slot returns
/// to its pre-transaction value, matching EIP-7928's no-op-write deduplication.
/// </remarks>
public sealed class BalFloorMeter(ulong staticFloor, ulong gasLimit)
{
    private readonly ulong _gasLimit = gasLimit;

    public ulong StaticFloor { get; } = staticFloor;
    public ulong BalDataBytes { get; private set; }

    /// <summary>The current floor: <c>static_floor + bal_data_bytes * FLOOR_GAS_PER_BYTE</c>.</summary>
    public ulong FloorGasUsed { get; private set; } = staticFloor;

    /// <returns><c>false</c> when the new floor exceeds the transaction gas limit (out of gas).</returns>
    public bool TryMeter(ulong balBytes)
    {
        BalDataBytes += balBytes;
        ulong newFloor = StaticFloor + BalDataBytes * Eip8279Constants.FloorGasPerByte;
        if (newFloor > _gasLimit)
        {
            return false;
        }

        FloorGasUsed = newFloor;
        return true;
    }

    /// <summary>Refunds storage-value bytes when a slot returns to its pre-transaction value.</summary>
    public void Refund(ulong balBytes)
    {
        BalDataBytes -= balBytes;
        FloorGasUsed = StaticFloor + BalDataBytes * Eip8279Constants.FloorGasPerByte;
    }
}
