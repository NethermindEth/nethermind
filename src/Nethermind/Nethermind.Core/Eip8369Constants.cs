// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Constants of EIP-8369 VOPS profiles for FOCIL eligibility.
/// https://eips.ethereum.org/EIPS/eip-8369
/// </summary>
/// <remarks>
/// EIP-8369 is Informational and leaves its budget caps to the Standards Track EIP that enforces them; EIP-7805
/// § Profile 2 inclusion claims and per IL budgets fixes the values here. <c>IBlocksConfig.FocilProfile2MaxVerifyGas</c>
/// is the operator-facing mirror, as <c>ITxPoolConfig.FrameTxMaxVerifyGas</c> is for EIP-8141's own cap.
/// </remarks>
public static class Eip8369Constants
{
    /// <summary><c>MAX_VERIFY_GAS_PER_IL</c>: the VERIFY budget one inclusion list may spend across its
    /// Profile 2 entries.</summary>
    public const ulong MaxVerifyGasPerIl = 1UL << 20;

    /// <summary><c>MAX_VERIFY_GAS_PER_TX</c>: the VERIFY budget cost above which a frame transaction is not a
    /// Profile 2 candidate, so FOCIL never enforces its omission.</summary>
    public const ulong MaxVerifyGasPerTx = 1UL << 18;

    /// <summary><c>AA_VOPS_SLOT_COUNT</c>: the leading storage slots of <c>sender</c> and <c>payer</c> a Profile 2
    /// validation prefix may read.</summary>
    public const ulong AaVopsSlotCount = 4;

    /// <summary><c>MAX_INCLUSION_LIST_CLAIMS</c>: the builder claims one payload may carry.</summary>
    public const int MaxInclusionListClaims = 1 << 12;
}
