// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.TxPool.Comparison;
using NUnit.Framework;

namespace Nethermind.TxPool.Test.Comparison;

/// <summary>Replacement of EIP-7999 <c>max_fee</c> frame transactions, which bump the budget rather than a per-gas cap.</summary>
public class CompareReplacedTxByFeeTests
{
    // The replacement doubles its gas, so its implied per-gas cap falls although its budget rises by 10%.
    [TestCase(1_100ul, 110ul, TxComparisonResult.TakeNew, TestName = "Compare_MaxFee_BumpedByTenPercent_TakesNew")]
    [TestCase(1_099ul, 110ul, TxComparisonResult.KeepOld, TestName = "Compare_MaxFee_BumpedByLess_KeepsOld")]
    [TestCase(1_100ul, 109ul, TxComparisonResult.KeepOld, TestName = "Compare_MaxFee_TipBumpedByLess_KeepsOld")]
    public void Compare_MaxFeeShape_BumpsTheBudget(ulong newMaxFee, ulong newTip, int expected)
    {
        Transaction oldTx = MaxFeeTx(maxFee: 1_000, tip: 100, gasLimit: 100);
        Transaction newTx = MaxFeeTx(newMaxFee, newTip, gasLimit: 200);

        Assert.That(CompareReplacedTxByFee.Instance.Compare(newTx, oldTx), Is.EqualTo(expected));
    }

    [TestCase(2_000ul, TxComparisonResult.TakeNew, TestName = "CompareBlob_MaxFee_Doubled_TakesNew")]
    [TestCase(1_999ul, TxComparisonResult.KeepOld, TestName = "CompareBlob_MaxFee_LessThanDoubled_KeepsOld")]
    public void CompareBlob_MaxFeeShape_DoublesTheBudget(ulong newMaxFee, int expected)
    {
        Transaction oldTx = MaxFeeTx(maxFee: 1_000, tip: 100, gasLimit: 100);
        Transaction newTx = MaxFeeTx(newMaxFee, tip: 200, gasLimit: 200);
        oldTx.BlobVersionedHashes = newTx.BlobVersionedHashes = [new byte[32]];

        Assert.That(CompareReplacedBlobTx.Instance.Compare(newTx, oldTx), Is.EqualTo(expected));
    }

    private static Transaction MaxFeeTx(UInt256 maxFee, UInt256 tip, ulong gasLimit) => new()
    {
        Type = TxType.FrameTx,
        GasLimit = gasLimit,
        GasPrice = tip,
        MaxFee = maxFee,
        DecodedMaxFeePerGas = FrameTxValidation.ImpliedMaxFeePerGas(maxFee, gasLimit),
    };
}
