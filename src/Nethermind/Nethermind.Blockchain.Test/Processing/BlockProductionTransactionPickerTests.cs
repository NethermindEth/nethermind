//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System.Collections.Generic;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Processing
{
    [Parallelizable(ParallelScope.All)]
    public class BlockProductionTransactionPickerTests
    {
        public static IEnumerable<TestCaseData> CanAddTestCases
        {
            get
            {
                Block fullBlock = Build.A.Block
                    .WithGasLimit(GasCostOf.Transaction * 10)
                    .WithGasUsed(GasCostOf.Transaction * 10 - 100)
                    .TestObject;
                
                Block block = Build.A.Block
                    .WithGasLimit(GasCostOf.Transaction * 10)
                    .TestObject;
                
                Transaction simpleTransfer = Build.A.Transaction
                    .To(TestItem.AddressB)
                    .WithValue(100.GWei())
                    .SignedAndResolved(TestItem.PrivateKeyC)
                    .TestObject;
                
                Transaction noSender = Build.A.Transaction.TestObject;
                
                Transaction highGasLimit = Build.A.Transaction
                    .WithGasLimit(GasCostOf.Transaction * 11)
                    .SignedAndResolved(TestItem.PrivateKeyC)
                    .TestObject;

                BlockProcessor.TransactionsInBlock transactionsInBlock = new();
                IStateProvider stateProvider = new StateProvider(new TrieStore(new MemDb(), LimboLogs.Instance), new MemDb(), LimboLogs.Instance);
                stateProvider.CreateAccount(TestItem.AddressC, 1000.GWei());
                stateProvider.CreateAccount(TestItem.AddressD, 1.GWei());
                stateProvider.UpdateCodeHash(TestItem.AddressD, TestItem.KeccakH, London.Instance);
                stateProvider.Commit(London.Instance);
                
                yield return new TestCaseData(block, simpleTransfer, transactionsInBlock, stateProvider)
                {
                    ExpectedResult = BlockProcessor.TxAction.Add, 
                    TestName = "Transaction added."
                };
                
                yield return new TestCaseData(fullBlock, simpleTransfer, transactionsInBlock, stateProvider)
                {
                    ExpectedResult = BlockProcessor.TxAction.Stop, 
                    TestName = "Full block."
                };
                
                yield return new TestCaseData(block, noSender, transactionsInBlock, stateProvider)
                {
                    ExpectedResult = BlockProcessor.TxAction.Skip, 
                    TestName = "No sender."
                };
                
                yield return new TestCaseData(block, highGasLimit, transactionsInBlock, stateProvider)
                {
                    ExpectedResult = BlockProcessor.TxAction.Skip, 
                    TestName = "Gas limit exceeded."
                };
                
                Transaction simpleTransfer2 = Build.A.Transaction
                    .To(TestItem.AddressB)
                    .WithValue(200.GWei())
                    .SignedAndResolved(TestItem.PrivateKeyC)
                    .TestObject;

                transactionsInBlock.Add(simpleTransfer2);
                
                yield return new TestCaseData(block, simpleTransfer2, transactionsInBlock, stateProvider)
                {
                    ExpectedResult = BlockProcessor.TxAction.Skip, 
                    TestName = "Transaction already added."
                };

                Transaction calldataFirst = Build.A.Transaction
                    .WithData(Block.BaseMaxCallDataPerBlock / 2)
                    .SignedAndResolved(TestItem.PrivateKeyC)
                    .TestObject;
                
                transactionsInBlock.Add(calldataFirst);
                
                Transaction calldataSecond = Build.A.Transaction
                    .WithData(Block.BaseMaxCallDataPerBlock / 2)
                    .WithValue(2.GWei())
                    .SignedAndResolved(TestItem.PrivateKeyC)
                    .TestObject;
                
                yield return new TestCaseData(block, calldataSecond, transactionsInBlock, stateProvider)
                {
                    ExpectedResult = BlockProcessor.TxAction.Add, 
                    TestName = "Below EIP-4488 limit."
                };

                Transaction calldataOverLimit = Build.A.Transaction
                    .WithData(Block.BaseMaxCallDataPerBlock / 2 * 3)
                    .SignedAndResolved(TestItem.PrivateKeyC)
                    .TestObject;
                
                yield return new TestCaseData(block, calldataOverLimit, transactionsInBlock, stateProvider)
                {
                    ExpectedResult = BlockProcessor.TxAction.Skip, 
                    TestName = "Over EIP-4488 limit."
                };
                
                Transaction contractTransaction = Build.A.Transaction
                    .SignedAndResolved(TestItem.PrivateKeyD)
                    .TestObject;
                
                yield return new TestCaseData(block, contractTransaction, transactionsInBlock, stateProvider)
                {
                    ExpectedResult = BlockProcessor.TxAction.Skip, 
                    TestName = "EIP-3607 contract sender."
                };
                
                Transaction wrongNonce = Build.A.Transaction
                    .WithNonce(2)
                    .SignedAndResolved(TestItem.PrivateKeyC)
                    .TestObject;
                
                yield return new TestCaseData(block, wrongNonce, transactionsInBlock, stateProvider)
                {
                    ExpectedResult = BlockProcessor.TxAction.Skip, 
                    TestName = "Wrong nonce."
                };
                
                Transaction lowBalance = Build.A.Transaction
                    .WithValue(1000.Ether())
                    .SignedAndResolved(TestItem.PrivateKeyC)
                    .TestObject;
                
                yield return new TestCaseData(block, lowBalance, transactionsInBlock, stateProvider)
                {
                    ExpectedResult = BlockProcessor.TxAction.Skip, 
                    TestName = "Low balance."
                };
                
                Transaction lowMaxFee = Build.A.Transaction
                    .WithValue(999.GWei())
                    .WithGasLimit(1000)
                    .WithMaxFeePerGas(1.GWei())
                    .WithType(TxType.EIP1559)
                    .SignedAndResolved(TestItem.PrivateKeyC)
                    .TestObject;
                
                yield return new TestCaseData(block, lowMaxFee, transactionsInBlock, stateProvider)
                {
                    ExpectedResult = BlockProcessor.TxAction.Skip, 
                    TestName = "Low balance to Max Fee."
                };
                
                Transaction serviceTransaction = Build.A.Transaction
                    .WithValue(1000.Ether())
                    .WithGasPrice(UInt256.Zero)
                    .WithIsServiceTransaction(true)
                    .SignedAndResolved(TestItem.PrivateKeyE)
                    .TestObject;
                
                yield return new TestCaseData(block, serviceTransaction, transactionsInBlock, stateProvider)
                {
                    ExpectedResult = BlockProcessor.TxAction.Skip, 
                    TestName = "Service transaction."
                };
            }
        }

        [TestCaseSource(nameof(CanAddTestCases))]
        public BlockProcessor.TxAction can_add_transaction(Block block, Transaction currentTx, BlockProcessor.TransactionsInBlock transactionsInBlock, IStateProvider stateProvider)
        {
            IReleaseSpec releaseSpec = new OverridableReleaseSpec(London.Instance) { IsEip4488Enabled = true };
            ISpecProvider specProvider = new SingleReleaseSpecProvider(releaseSpec, 1);
            BlockProcessor.BlockProductionTransactionPicker picker = new(specProvider);
            return picker.CanAddTransaction(block, currentTx, transactionsInBlock, stateProvider).Action;
        }
    }
}
