// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions
{
    public class GeneratedTxSourceTests
    {
        [Test]
        public void Reseal_generated_transactions()
        {
            ITxSource innerSource = Substitute.For<ITxSource>();
            ITxSealer txSealer = Substitute.For<ITxSealer>();
            IStateReader stateReader = Substitute.For<IStateReader>();

            BlockHeader parent = Build.A.BlockHeader.TestObject;
            long gasLimit = long.MaxValue;
            Transaction poolTx = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).TestObject;
            GeneratedTransaction generatedTx = Build.A.GeneratedTransaction.WithSenderAddress(TestItem.AddressB).TestObject;
            innerSource.GetTransactions(parent, gasLimit).Returns(new[] { poolTx, generatedTx });

            GeneratedTxSource txSource = new(innerSource, txSealer, stateReader, LimboLogs.Instance);

            txSource.GetTransactions(parent, gasLimit).ToArray();

            txSealer.Received().Seal(generatedTx, TxHandlingOptions.ManagedNonce | TxHandlingOptions.AllowReplacingSignature);
            txSealer.DidNotReceive().Seal(poolTx, Arg.Any<TxHandlingOptions>());
        }
    }
}
