// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Taiko.TaikoSpec;

/// <summary>
/// One entry in the chainspec-driven Unzen ZK gas schedule list. The schedule with the largest
/// <see cref="Timestamp"/> not exceeding the active block timestamp is in effect; if every
/// configured schedule activates in the future, the earliest one acts as the floor so the meter
/// always has a multiplier table.
/// </summary>
/// <remarks>
/// <para>
/// The opcode map is sparse and indexed by opcode byte (0x00–0xff); the precompile map is sparse
/// and keyed by the precompile's full 20-byte address (hex string) so canonical EVM precompiles
/// (e.g. <c>0x…0001</c> ecrecover) and Taiko-extended precompiles (e.g. <c>0x…010001</c> L1Sload,
/// <c>0x…010002</c> L1StaticCall) live in the same table without colliding by low byte.
/// </para>
/// <para>
/// Listed entries take their multiplier (0–<see cref="ushort.MaxValue"/>); everything else falls
/// back to <see cref="Nethermind.Taiko.ZkGas.ZkGasSchedule.FailsafeMultiplier"/>. Because the
/// fail-safe is the fill, a chainspec must fully restate the table it pins — no implicit merging
/// with prior schedules.
/// </para>
/// </remarks>
public class TaikoUnzenZkGasSchedule
{
    /// <summary>Block timestamp at which this schedule becomes active.</summary>
    public ulong Timestamp { get; set; }

    /// <summary>Sparse opcode-byte → multiplier map for this schedule, or <c>null</c> when unset.</summary>
    public Dictionary<long, long>? OpcodeMultipliers { get; set; }

    /// <summary>Sparse precompile-address (hex string) → multiplier map for this schedule, or <c>null</c> when unset.</summary>
    public Dictionary<string, long>? PrecompileMultipliers { get; set; }
}
