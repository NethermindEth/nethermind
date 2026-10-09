// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm;

/// <summary>The fields of the block context that execution read through an opcode, and whether it ran out of gas.</summary>
/// <remarks>
/// A frame that runs out of gas may have been charged for accessing the coinbase before reading it, a charge that
/// depends on which address the coinbase is (EIP-3651) while no state read shows it.
/// </remarks>
[Flags]
public enum BlockContextReads : byte
{
    None = 0,
    Coinbase = 1,
    Timestamp = 2,
    GasLimit = 4,
    PrevRandao = 8,
    OutOfGas = 16
}
