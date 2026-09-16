// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

/// <summary>
/// Pins why the shipped XDC configs set <c>Blocks.MinGasPrice</c> to zero.
/// </summary>
/// <remarks>
/// XDC's base fee is a constant equal to the gas price floor its reference client demands, so a transaction paying
/// exactly that floor - the reference's minimum, and what its gas price oracle suggests - has no priority fee left.
/// <see cref="MinGasPriceTxFilter"/> compares the priority fee, so any non-zero <c>Blocks.MinGasPrice</c> makes the
/// block producer skip transactions the reference client both accepts and mines. The reference has no equivalent
/// floor to match: its <c>NewTransactionsByPriceAndNonce</c> takes no base fee and orders on the raw gas price, so
/// nothing in its block building converts to an effective tip.
/// </remarks>
[Parallelizable(ParallelScope.All)]
internal class BlockProductionMinGasPriceTests
{
    [TestCase(0ul, true, TestName = "Zero floor includes a transaction paying the chain minimum")]
    [TestCase(1ul, false, TestName = "Default one wei floor skips a transaction paying the chain minimum")]
    public void MinGasPriceTxFilter_TxAtChainMinimum_WithXdcBaseFee(ulong configuredFloor, bool expectedAllowed)
    {
        XdcReleaseSpec spec = new() { IsEip1559Enabled = true, BaseFeeCalculator = new XdcBaseFeeCalculator() };
        MinGasPriceTxFilter filter = new(new BlocksConfig { MinGasPrice = configuredFloor });

        Transaction tx = Build.A.Transaction
            .WithType(TxType.Legacy)
            .WithGasPrice(XdcConstants.DefaultMinGasPrice * XdcConstants.Gas50xMultiplier)
            .WithTo(TestItem.AddressC)
            .TestObject;

        AcceptTxResult result = filter.IsAllowed(tx, Build.A.BlockHeader.TestObject, spec);

        Assert.That((bool)result, Is.EqualTo(expectedAllowed), result.ToString());
    }
}
