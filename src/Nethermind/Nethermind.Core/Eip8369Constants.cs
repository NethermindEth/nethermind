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

    /// <summary><c>AA_VOPS_SLOT_COUNT</c>: the leading storage slots of <c>sender</c> and <c>payer</c> a Profile 2
    /// validation prefix may read.</summary>
    /// <remarks>
    /// EIP-8369 leaves the value to the enforcing Standards Track EIP ("candidate range 2 to 4, pending
    /// benchmarks"), and neither EIP-7805 nor EIP-8369 fixes it yet. The top of that range is taken until one does.
    /// </remarks>
    public const ulong AaVopsSlotCount = 4;

    /// <summary><c>MAX_INCLUSION_LIST_CLAIMS</c>: the builder claims one payload may carry.</summary>
    /// <remarks>A placeholder pending the EIP-7805 extension that encodes claims.</remarks>
    public const int MaxInclusionListClaims = 1024;
}
