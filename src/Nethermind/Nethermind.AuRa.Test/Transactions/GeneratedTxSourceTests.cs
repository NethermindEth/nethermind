//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
            innerSource.GetTransactions(parent, gasLimit).Returns(new[] {poolTx, generatedTx});
            
            var txSource = new GeneratedTxSource(innerSource, txSealer, stateReader, LimboLogs.Instance);

            txSource.GetTransactions(parent, gasLimit).ToArray();
            
            txSealer.Received().Seal(generatedTx, TxHandlingOptions.ManagedNonce | TxHandlingOptions.AllowReplacingSignature);
            txSealer.DidNotReceive().Seal(poolTx, Arg.Any<TxHandlingOptions>());
        }
    }
}
