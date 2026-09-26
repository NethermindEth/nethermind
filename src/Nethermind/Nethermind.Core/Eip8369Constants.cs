// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Constants of EIP-8369 VOPS profiles for FOCIL eligibility.
/// https://eips.ethereum.org/EIPS/eip-8369
/// </summary>
/// <remarks>
/// EIP-8369 is Informational and leaves its budget caps to the Standards Track EIP that will enforce them, so
/// the values here are local choices, not consensus constants. <c>IBlocksConfig.FocilProfile2MaxVerifyGas</c>
/// is the operator-facing mirror, as <c>ITxPoolConfig.FrameTxMaxVerifyGas</c> is for EIP-8141's own cap.
/// </remarks>
public static class Eip8369Constants
{
    /// <summary><c>MAX_VERIFY_GAS_PER_IL</c>: the VERIFY budget one inclusion list may spend across its
    /// Profile 2 entries. EIP-8369 gives <c>2**20</c> as a candidate value pending benchmarks.</summary>
    public const ulong MaxVerifyGasPerIl = 1UL << 20;

    /// <summary><c>MAX_VERIFY_GAS_PER_TX</c>: the VERIFY budget cost above which a frame transaction is not a
    /// Profile 2 candidate, so FOCIL never enforces its omission.</summary>
    /// <remarks>
    /// EIP-8369 pins only the ceiling — "up to <c>MAX_VERIFY_GAS_PER_IL</c>" — leaving a chain free to set it
    /// lower so several transactions share one list's budget. Taking the ceiling is the choice that admits the
    /// most transactions to enforcement, which is the side to err on while no enforcing EIP exists.
    /// </remarks>
    public const ulong MaxVerifyGasPerTx = MaxVerifyGasPerIl;
}
