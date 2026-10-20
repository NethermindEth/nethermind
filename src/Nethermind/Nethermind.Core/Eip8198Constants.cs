// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>EIP-8198: Quick Slots.</summary>
public static class Eip8198Constants
{
    /// <summary>
    /// <c>BASE_FEE_REFERENCE_SLOT_SECONDS</c>: the slot duration at which the base fee changes by at most
    /// <c>1 / BaseFeeMaxChangeDenominator</c> per block.
    /// </summary>
    /// <remarks>
    /// The maximum change scales with the slot duration read from the spacing of a block and its parent, so it is 1/8 at
    /// 12 s slots and 5/48 at 10 s slots. See <see cref="DefaultBaseFeeCalculator"/>.
    /// </remarks>
    public const ulong BaseFeeReferenceSlotSeconds = 12;
}
