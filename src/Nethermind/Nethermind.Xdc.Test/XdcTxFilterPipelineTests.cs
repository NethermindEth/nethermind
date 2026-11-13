// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
internal class XdcTxFilterPipelineTests
{
    private static readonly Address BlockSigner = TestItem.AddressA;

    [TestCase(false, false)]
    [TestCase(true, true)]
    public void Passes_the_context_to_the_inner_pipeline_unless_the_transaction_is_special(bool special, bool expected)
    {
        IXdcReleaseSpec spec = Substitute.For<IXdcReleaseSpec>();
        spec.IsEip1559Enabled.Returns(true);
        spec.BlockSignerContract.Returns(BlockSigner);
        spec.RandomizeSMCBinary.Returns(TestItem.AddressB);
        XdcTxFilterPipeline pipeline = new(new TxFilterPipelineBuilder(NullLogManager.Instance).WithBaseFeeFilter().Build);
        BlockHeader parent = Build.A.BlockHeader.WithNumber(10).WithBaseFee(1.GWei).TestObject;
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).WithTo(special ? BlockSigner : TestItem.AddressC)
            .WithMaxFeePerGas(5.GWei).WithMaxPriorityFeePerGas(1.GWei).TestObject;
        // A base fee above the transaction's max fee, which the inner pipeline's base-fee filter rejects.
        TxFilterContext context = new(parent, spec, 7.GWei);

        Assert.That(pipeline.Execute(tx, in context), Is.EqualTo(expected));
    }
}
