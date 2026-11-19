// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismBlockProductionTransactionPickerTests
{
    [Test]
    public void Local_byte_limit_still_applies_to_transactions_from_the_pool()
    {
        Transaction required = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithGasLimit(21_000).TestObject;
        Transaction optional = Build.A.Transaction.WithSenderAddress(TestItem.AddressB).WithGasLimit(21_000).TestObject;
        BlockToProduce block = new(Build.A.BlockHeader.WithGasLimit(1_000_000).TestObject, [required, optional], [])
        {
            TransactionsExemptFromByteLimit = new HashSet<Transaction>([required], ReferenceEqualityComparer.Instance),
        };
        IReadOnlyStateProvider state = Substitute.For<IReadOnlyStateProvider>();
        state.GetBalance(TestItem.AddressA).Returns(UInt256.MaxValue);
        OptimismBlockProductionTransactionPicker picker = new(new TestSpecProvider(Byzantium.Instance), maxTxLengthKilobytes: 0);
        HashSet<Transaction> considered = [];

        BlockProcessor.TxAction requiredAction = picker.CanAddTransaction(block, required, considered, state, 0, 0).Action;
        BlockProcessor.TxAction optionalAction = picker.CanAddTransaction(block, optional, considered, state, 0, 0).Action;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requiredAction, Is.EqualTo(BlockProcessor.TxAction.Add));
            Assert.That(optionalAction, Is.EqualTo(BlockProcessor.TxAction.Stop));
        }
    }
}
