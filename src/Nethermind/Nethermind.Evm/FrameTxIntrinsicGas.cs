// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Evm;

/// <summary>Gas an EIP-8141 frame transaction owes before any frame runs.</summary>
public static class FrameTxIntrinsicGas
{
    /// <summary>
    /// Computes the intrinsic gas of a frame transaction and, via <paramref name="floorGas"/>, the
    /// least gas it may be charged.
    /// </summary>
    /// <remarks>
    /// The intrinsic cost is <c>FRAME_TX_INTRINSIC_COST</c> + frames × <c>FRAME_TX_PER_FRAME_COST</c>
    /// + per-scheme signature verification + the calldata cost of the frame data and signature
    /// fields. The floor adds the same mandatory (non-data) costs to the data priced at
    /// <see cref="SpecGasCosts.TotalCostFloorPerToken"/>, mirroring
    /// <see cref="IntrinsicGasCalculator.CalculateFloorCost"/>: EIP-7976 prices every data byte as a
    /// non-zero token, EIP-7623 weights zero bytes lower. Both the rate and the token weighting are
    /// resolved from the spec so a frame transaction's floor cannot diverge from an ordinary
    /// transaction's under the same fork.
    /// </remarks>
    /// <param name="tx">The frame transaction being charged.</param>
    /// <param name="frames">The transaction's frames.</param>
    /// <param name="spec">The release spec in effect.</param>
    /// <param name="floorGas">The minimum chargeable gas, or 0 when floor pricing is not active.</param>
    /// <returns>The intrinsic gas charged before execution.</returns>
    public static ulong Calculate(Transaction tx, TxFrame[] frames, IReleaseSpec spec, out ulong floorGas)
    {
        ulong tokens = 0;
        ulong dataLength = 0;
        foreach (TxFrame frame in frames)
        {
            tokens += CountCalldataTokens(frame.Data.Span, spec);
            dataLength += (ulong)frame.Data.Length;
        }

        ulong signatureVerificationCost = 0;
        TxFrameSignature[]? signatures = tx.FrameSignatures;
        if (signatures is not null)
        {
            foreach (TxFrameSignature signature in signatures)
            {
                tokens += signature.Signer is null ? 0 : CountCalldataTokens(signature.Signer.Bytes, spec);
                tokens += CountCalldataTokens(signature.Msg.Span, spec);
                tokens += CountCalldataTokens(signature.Signature.Span, spec);
                dataLength += (ulong)(signature.Signer is null ? 0 : Address.Size)
                              + (ulong)signature.Msg.Length
                              + (ulong)signature.Signature.Length;
                signatureVerificationCost += signature.Scheme switch
                {
                    TxFrameSignature.SchemeArbitrary => Eip8141Constants.ArbitraryVerificationGasCost,
                    TxFrameSignature.SchemeSecp256k1 => Eip8141Constants.Secp256k1VerificationGasCost,
                    TxFrameSignature.SchemeP256 => Eip8141Constants.P256VerificationGasCost,
                    _ => 0,
                };
            }
        }

        ulong mandatoryGas = (ulong)Eip8141Constants.IntrinsicGasCost
                             + (ulong)frames.Length * (ulong)Eip8141Constants.PerFrameGasCost
                             + signatureVerificationCost;
        ulong floorTokens = spec.IsEip7976Enabled ? dataLength * spec.GasCosts.TxDataNonZeroMultiplier : tokens;
        floorGas = spec.IsEip7623Enabled ? mandatoryGas + floorTokens * spec.GasCosts.TotalCostFloorPerToken : 0;
        return mandatoryGas + tokens * GasCostOf.TxDataZero;
    }

    private static ulong CountCalldataTokens(ReadOnlySpan<byte> data, IReleaseSpec spec)
    {
        int zeros = data.CountZeros();
        return (ulong)zeros + (ulong)(data.Length - zeros) * spec.GasCosts.TxDataNonZeroMultiplier;
    }
}
