// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

[Parallelizable(ParallelScope.All)]
public class AuRaMergeTxFilterTests
{
    [TestCase(true, "MaxFeePerGas too low. MaxFeePerGas: 5000000000, BaseFee: 7000000000, MaxPriorityFeePerGas:1000000000, Block number: 11")]
    [TestCase(false, "EffectivePriorityFeePerGas too low 0 < 100000000000, BaseFee: 7000000000")]
    public void Passes_the_context_to_the_filter_of_its_side_of_the_merge(bool postMerge, string rejection)
    {
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.IsPostMerge(Arg.Any<BlockHeader>()).Returns(postMerge);
        AuRaMergeTxFilter filter = new(poSSwitcher,
            preMergeTxFilter: new MinGasPriceTxFilter(new BlocksConfig { MinGasPrice = 100.GWei }),
            postMergeTxFilter: new BaseFeeTxFilter());
        ReleaseSpec london = new() { IsEip1559Enabled = true };
        // The parent used its gas target at 1 gwei, so its next base fee is 1 gwei; the context carries 7 gwei.
        BlockHeader parent = Build.A.BlockHeader.WithNumber(10).WithBaseFee(1.GWei).WithGasLimit(30_000_000).WithGasUsed(15_000_000).TestObject;
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).WithMaxFeePerGas(5.GWei).WithMaxPriorityFeePerGas(1.GWei).TestObject;
        TxFilterContext context = new(parent, london, 7.GWei);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(filter.IsAllowed(tx, new TxFilterContext(parent, london)).ToString(), Is.EqualTo(postMerge
                ? nameof(AcceptTxResult.Accepted)
                : $"{AcceptTxResult.FeeTooLow}, EffectivePriorityFeePerGas too low 1000000000 < 100000000000, BaseFee: 1000000000"));
            Assert.That(filter.IsAllowed(tx, in context).ToString(), Is.EqualTo($"{AcceptTxResult.FeeTooLow}, {rejection}"));
        }
    }
}
