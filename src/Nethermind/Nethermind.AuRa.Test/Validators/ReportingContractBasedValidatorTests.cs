﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Validators
{
    [Parallelizable(ParallelScope.All)]
    public class ReportingContractBasedValidatorTests
    {
        private static readonly Address NodeAddress = TestItem.AddressB;
        private static readonly Address MaliciousMinerAddress = TestItem.AddressA;
        
        [Test]
        public void Report_malicious_sends_transaction([Values(true, false)] bool reportingValidator)
        {
            var context = new TestContext(true);
            var proof = TestItem.KeccakA.Bytes;
            var transaction = Build.A.Transaction.TestObject;
            context.ReportingValidatorContract.ReportMalicious(MaliciousMinerAddress, 5, proof).Returns(transaction);
            context.Validator.ReportMalicious(reportingValidator ? MaliciousMinerAddress : NodeAddress, 5, proof, IReportingValidator.MaliciousCause.DuplicateStep);
            context.TxSender.Received(reportingValidator ? 1 : 0).SendTransaction(transaction, TxHandlingOptions.ManagedNonce | TxHandlingOptions.PersistentBroadcast);
        }
        
        [Test]
        public void Report_benign_sends_transaction([Values(0, 5)] long blockNumber)
        {
            var context = new TestContext(true);
            var transaction = Build.A.Transaction.TestObject;
            context.ReportingValidatorContract.ReportBenign(MaliciousMinerAddress, (UInt256) blockNumber).Returns(transaction);
            context.Validator.ReportBenign(MaliciousMinerAddress, blockNumber, IReportingValidator.BenignCause.FutureBlock);
            context.TxSender.Received(1).SendTransaction(transaction, TxHandlingOptions.ManagedNonce | TxHandlingOptions.PersistentBroadcast);            
        }
        
        [Test]
        public void Resend_malicious_transactions([Values(0, 5, 15)] int validatorsToReport, [Values(1, 3)] long blockNumber)
        {
            var cache = new ReportingContractBasedValidator.Cache();
            var proof = TestItem.KeccakA.Bytes;
            var transaction = Build.A.Transaction.TestObject;
            var context = new TestContext(false, cache);
            for (ulong i = 5; i < 20; i++)
            {
                context.ReportingValidatorContract.ReportMalicious(MaliciousMinerAddress, i, proof).Returns(transaction);
                context.Validator.ReportMalicious(MaliciousMinerAddress, (long) i, proof, IReportingValidator.MaliciousCause.DuplicateStep);
            }
            
            context = new TestContext(false, cache);
            for (ulong i = 5; i < 20; i++)
            {
                context.ReportingValidatorContract.ReportMalicious(MaliciousMinerAddress, i, proof).Returns(transaction);
            }

            var block = Build.A.Block.WithNumber(blockNumber).TestObject;
            
            context.ContractBasedValidator.ValidatorContract
                .ShouldValidatorReport(NodeAddress, MaliciousMinerAddress, Arg.Any<UInt256>(), block.Header)
                .Returns(0 < validatorsToReport, Enumerable.Range(1, 15).Select(i => i < validatorsToReport).ToArray());

            
            bool isPosDao = blockNumber >= context.PosdaoTransition;
            
            // resend transactions
            context.Validator.OnBlockProcessingEnd(block, Array.Empty<TxReceipt>());
            
            // not resending on next block!
            var childBlock = Build.A.Block.WithParent(block).TestObject;
            context.Validator.OnBlockProcessingEnd(childBlock, Array.Empty<TxReceipt>());
            
            context.TxSender.Received(isPosDao ? Math.Min(ReportingContractBasedValidator.MaxQueuedReports, validatorsToReport) : 0)
                .SendTransaction(Arg.Any<Transaction>(), TxHandlingOptions.ManagedNonce | TxHandlingOptions.PersistentBroadcast);

        }
        
        [Test]
        public void Adds_transactions_to_block([Values(0, 5, 15)] int validatorsToReport, [Values(0, 2)] long parentBlockNumber, [Values(false, true)] bool emitInitChangeCallable)
        {
            var context = new TestContext(true);
            var proof = TestItem.KeccakA.Bytes;
            var transaction = Build.A.Transaction.TestObject;
            context.ContractBasedValidator.Validators = new[] {MaliciousMinerAddress, NodeAddress};
            for (ulong i = 5; i < 20; i++)
            {
                context.ReportingValidatorContract.ReportMalicious(MaliciousMinerAddress, i, proof).Returns(transaction);
                context.Validator.ReportMalicious(MaliciousMinerAddress, (long) i, proof, IReportingValidator.MaliciousCause.DuplicateStep);
            }

            var parent = Build.A.BlockHeader.WithNumber(parentBlockNumber).TestObject;
            bool isPosDao = parentBlockNumber + 1 >= context.PosdaoTransition;
            context.ContractBasedValidator.ValidatorContract
                .ShouldValidatorReport(NodeAddress, MaliciousMinerAddress, Arg.Any<UInt256>(), parent)
                .Returns(0 < validatorsToReport, Enumerable.Range(1, 15).Select(i => i < validatorsToReport).ToArray());

            var initChangeTransaction = Build.A.Transaction.TestObject;
            var initChangeTransactionAdded = emitInitChangeCallable && isPosDao;
            context.ContractBasedValidator.ValidatorContract.EmitInitiateChangeCallable(parent).Returns(emitInitChangeCallable);
            context.ContractBasedValidator.ValidatorContract.EmitInitiateChange().Returns(initChangeTransaction);

            var transactions = context.Validator.GetTransactions(parent, 3000000).ToArray();
            transactions.Should().HaveCount(Math.Min(ReportingContractBasedValidator.MaxReportsPerBlock, isPosDao ? validatorsToReport : 0) +  (initChangeTransactionAdded ? 1 : 0));
            if (initChangeTransactionAdded)
            {
                transactions.First().Should().Be(initChangeTransaction);
            }
        }
        
        [Test]
        public void Reports_skipped_blocks()
        {
            var context = new TestContext(false, initialValidators: new[] {TestItem.AddressA, NodeAddress, TestItem.AddressC, TestItem.AddressD});
            var parent = Build.A.BlockHeader.WithNumber(10).WithAura(10).TestObject;
            var header = Build.A.BlockHeader.WithParent(parent).WithAura(20).TestObject;
            context.Validator.TryReportSkipped(header, parent);
            context.ReportingValidatorContract.Received(1).ReportBenign(TestItem.AddressA, (UInt256) header.Number);
            context.ReportingValidatorContract.Received(1).ReportBenign(TestItem.AddressC, (UInt256) header.Number);
            context.ReportingValidatorContract.Received(1).ReportBenign(TestItem.AddressD, (UInt256) header.Number);
            context.ReportingValidatorContract.Received(0).ReportBenign(NodeAddress, (UInt256) header.Number);
        }
        
        public class TestContext
        {
            public readonly int PosdaoTransition = 3;
            public ReportingContractBasedValidator Validator { get; }
            public ITxSender TxSender { get; }
            public IReportingValidatorContract ReportingValidatorContract { get; }
            public ContractBasedValidator ContractBasedValidator { get; }

            public TestContext(bool forSealing, ReportingContractBasedValidator.Cache cache = null, Address[] initialValidators = null)
            {
                var parentHeader = Build.A.BlockHeader.TestObject;
                var validatorContract = Substitute.For<IValidatorContract>();
                var validators = initialValidators ?? new[] {MaliciousMinerAddress, NodeAddress};
                validatorContract.GetValidators(parentHeader).Returns(validators);
                
                ContractBasedValidator = new ContractBasedValidator(
                    validatorContract, 
                    Substitute.For<IBlockTree>(), 
                    Substitute.For<IReceiptFinder>(), 
                    Substitute.For<IValidatorStore>(), 
                    Substitute.For<IValidSealerStrategy>(), 
                    Substitute.For<IBlockFinalizationManager>(), 
                    parentHeader,
                    LimboLogs.Instance, 
                    0,
                    PosdaoTransition,
                    forSealing);
                
                ContractBasedValidator.Validators ??= validators;
            
                ReportingValidatorContract = Substitute.For<IReportingValidatorContract>();
                ReportingValidatorContract.NodeAddress.Returns(NodeAddress);
                
                TxSender = Substitute.For<ITxSender>();
                var txPool = Substitute.For<ITxPool>();
                var stateProvider = Substitute.For<IStateProvider>();
                stateProvider.GetNonce(ReportingValidatorContract.NodeAddress).Returns(UInt256.One);
                
                Validator = new ReportingContractBasedValidator(
                    ContractBasedValidator,
                    ReportingValidatorContract,
                    PosdaoTransition,
                    TxSender,
                    txPool,
                    stateProvider,
                    cache ?? new ReportingContractBasedValidator.Cache(),
                    LimboLogs.Instance);
            }
        }
    }
}
