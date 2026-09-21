// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.TxPool;

/// <summary>
/// The MATCHA width an additional pending EIP-8250 keyed-nonce frame transaction spends, in the same gas unit a
/// sender earns from its finalized frame transactions.
/// </summary>
/// <remarks>
/// EIP-8141 MATCHA <c>charge(tx) = ceil(safety_factor * admission_gas(tx))</c>. The safety factor is carried in
/// permille so no floating point enters admission; the MATCHA post leaves its calibrated value to clients, so a
/// deployment sets it and the default leaves the charge at the measured admission gas. The single admission and
/// revalidation charge live here so both spend the same amount.
/// </remarks>
internal static class FrameTxWidthCharge
{
    private const ulong Permille = 1000;

    public const int Eip8141PublicMempoolBaseline = 1;

    public static UInt256 For(Transaction tx, ulong safetyFactorPermille)
    {
        UInt256 scaled = (UInt256)FrameTxValidation.AdmissionGas(tx) * safetyFactorPermille;
        return (scaled + (Permille - 1)) / Permille;
    }
}
