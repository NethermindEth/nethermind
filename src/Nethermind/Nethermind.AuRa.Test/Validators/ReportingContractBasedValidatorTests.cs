// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
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
            TestContext context = new(true);
            byte[] proof = TestItem.KeccakA.BytesToArray();
            Transaction transaction = Build.A.Transaction.TestObject;
            context.ReportingValidatorContract.ReportMalicious(MaliciousMinerAddress, 5, proof).Returns(transaction);
            context.Validator.ReportMalicious(reportingValidator ? MaliciousMinerAddress : NodeAddress, 5, proof, IReportingValidator.MaliciousCause.DuplicateStep);
            context.TxSender.Received(reportingValidator ? 1 : 0).SendTransaction(transaction, TxHandlingOptions.ManagedNonce | TxHandlingOptions.PersistentBroadcast);
        }

        [Test]
        public void Report_benign_sends_transaction([Values(0, 5)] long blockNumber)
        {
            TestContext context = new(true);
            Transaction transaction = Build.A.Transaction.TestObject;
            context.ReportingValidatorContract.ReportBenign(MaliciousMinerAddress, (UInt256)blockNumber).Returns(transaction);
            context.Validator.ReportBenign(MaliciousMinerAddress, blockNumber, IReportingValidator.BenignCause.FutureBlock);
            context.TxSender.Received(1).SendTransaction(transaction, TxHandlingOptions.ManagedNonce);
        }

        [Test]
        public void Resend_malicious_transactions([Values(0, 5, 15)] int validatorsToReport, [Values(1, 4)] long blockNumber)
        {
            ReportingContractBasedValidator.Cache cache = new();
            byte[] proof = TestItem.KeccakA.BytesToArray();
            Transaction transaction = Build.A.Transaction.TestObject;
            TestContext context = new(false, cache);
            for (ulong i = 5; i < 20; i++)
            {
                context.ReportingValidatorContract.ReportMalicious(MaliciousMinerAddress, i, proof).Returns(transaction);
                context.Validator.ReportMalicious(MaliciousMinerAddress, (long)i, proof, IReportingValidator.MaliciousCause.DuplicateStep);
            }

            context = new TestContext(false, cache);
            for (ulong i = 5; i < 20; i++)
            {
                context.ReportingValidatorContract.ReportMalicious(MaliciousMinerAddress, i, proof).Returns(transaction);
            }

            Block block = Build.A.Block.WithNumber(blockNumber).TestObject;

            context.ContractBasedValidator.ValidatorContract
                .ShouldValidatorReport(Arg.Is<BlockHeader>(h => h.Number == blockNumber - 1), NodeAddress, MaliciousMinerAddress, Arg.Any<UInt256>())
                .Returns(0 < validatorsToReport, Enumerable.Range(1, 15).Select(i => i < validatorsToReport).ToArray());

            context.ContractBasedValidator.BlockTree.FindHeader(Arg.Any<Keccak>(), BlockTreeLookupOptions.None)
                .Returns(Build.A.BlockHeader.WithNumber(blockNumber - 1).TestObject);

            bool isPosDao = blockNumber >= context.PosdaoTransition;

            // resend transactions
            context.Validator.OnBlockProcessingEnd(block, Array.Empty<TxReceipt>());

            // not resending on next block!
            Block childBlock = Build.A.Block.WithParent(block).TestObject;
            context.Validator.OnBlockProcessingEnd(childBlock, Array.Empty<TxReceipt>());

            context.TxSender.Received(isPosDao ? Math.Min(ReportingContractBasedValidator.MaxQueuedReports, validatorsToReport) : 0)
                .SendTransaction(Arg.Any<Transaction>(), TxHandlingOptions.ManagedNonce | TxHandlingOptions.PersistentBroadcast);

        }

        [Test]
        public void Adds_transactions_to_block([Values(0, 5, 15)] int validatorsToReport, [Values(0, 2, 10, 20)] long parentBlockNumber, [Values(false, true)] bool emitInitChangeCallable)
        {
            TestContext context = new(true);
            byte[] proof = TestItem.KeccakA.BytesToArray();
            Transaction transaction = Build.A.Transaction.TestObject;
            context.ContractBasedValidator.Validators = new[] { MaliciousMinerAddress, NodeAddress };
            ulong startReportBlockNumber = 5;
            for (ulong i = startReportBlockNumber; i < startReportBlockNumber + (ulong)validatorsToReport; i++)
            {
                context.ReportingValidatorContract.ReportMalicious(MaliciousMinerAddress, i, proof).Returns(transaction);
                context.Validator.ReportMalicious(MaliciousMinerAddress, (long)i, proof, IReportingValidator.MaliciousCause.DuplicateStep);
            }

            BlockHeader parent = Build.A.BlockHeader.WithNumber(parentBlockNumber).TestObject;
            bool isPosDao = parentBlockNumber + 1 >= context.PosdaoTransition;
            context.ContractBasedValidator.ValidatorContract
                .ShouldValidatorReport(parent, NodeAddress, MaliciousMinerAddress, Arg.Any<UInt256>())
                .Returns(0 < validatorsToReport, Enumerable.Range(1, 15).Select(i => i < validatorsToReport).ToArray());

            Transaction initChangeTransaction = Build.A.Transaction.TestObject;
            bool initChangeTransactionAdded = emitInitChangeCallable && isPosDao;
            context.ContractBasedValidator.ValidatorContract.EmitInitiateChangeCallable(parent).Returns(emitInitChangeCallable);
            context.ContractBasedValidator.ValidatorContract.EmitInitiateChange().Returns(initChangeTransaction);

            Transaction[] transactions = context.Validator.GetTransactions(parent, 3000000).ToArray();
            int addedMaliciousTransactions = (int)Math.Min(validatorsToReport, Math.Max(0, parentBlockNumber - (long)startReportBlockNumber));
            transactions.Should().HaveCount(Math.Min(ReportingContractBasedValidator.MaxReportsPerBlock, isPosDao ? addedMaliciousTransactions : 0) + (initChangeTransactionAdded ? 1 : 0));
            if (initChangeTransactionAdded)
            {
                transactions.First().Should().Be(initChangeTransaction);
            }
        }

        [Test]
        public void Reports_skipped_blocks()
        {
            TestContext context = new(false, initialValidators: new[] { TestItem.AddressA, NodeAddress, TestItem.AddressC, TestItem.AddressD });
            context.ReportingValidatorContract.ReportBenign(Arg.Any<Address>(), Arg.Any<UInt256>()).Returns(new GeneratedTransaction());
            BlockHeader parent = Build.A.BlockHeader.WithNumber(10).WithAura(10).TestObject;
            BlockHeader header = Build.A.BlockHeader.WithParent(parent).WithAura(20).TestObject;
            context.Validator.TryReportSkipped(header, parent);
            context.ReportingValidatorContract.Received(1).ReportBenign(TestItem.AddressA, (UInt256)header.Number);
            context.ReportingValidatorContract.Received(1).ReportBenign(TestItem.AddressC, (UInt256)header.Number);
            context.ReportingValidatorContract.Received(1).ReportBenign(TestItem.AddressD, (UInt256)header.Number);
            context.ReportingValidatorContract.Received(0).ReportBenign(NodeAddress, (UInt256)header.Number);
            context.TxSender.Received(3).SendTransaction(Arg.Is<Transaction>(t => t is GeneratedTransaction), TxHandlingOptions.ManagedNonce);
        }

        [Test]
        public void Report_ignores_duplicates_in_same_block()
        {
            TestContext context = new(true, initialValidators: new[] { TestItem.AddressA, NodeAddress, TestItem.AddressC });
            Transaction transaction = Build.A.Transaction.TestObject;
            context.ReportingValidatorContract.ReportBenign(Arg.Any<Address>(), Arg.Any<UInt256>()).Returns(transaction);
            context.ReportingValidatorContract.ReportMalicious(Arg.Any<Address>(), Arg.Any<UInt256>(), Arg.Any<byte[]>()).Returns(transaction);

            context.Validator.ReportBenign(MaliciousMinerAddress, 100, IReportingValidator.BenignCause.FutureBlock); // sent
            context.Validator.ReportBenign(MaliciousMinerAddress, 100, IReportingValidator.BenignCause.IncorrectProposer); // ignored
            context.Validator.ReportBenign(MaliciousMinerAddress, 100, IReportingValidator.BenignCause.FutureBlock); // ignored
            context.Validator.ReportBenign(MaliciousMinerAddress, 100, IReportingValidator.BenignCause.IncorrectProposer); // ignored
            context.Validator.ReportMalicious(MaliciousMinerAddress, 100, Bytes.Empty, IReportingValidator.MaliciousCause.DuplicateStep); // sent
            context.Validator.ReportMalicious(MaliciousMinerAddress, 100, Bytes.Empty, IReportingValidator.MaliciousCause.DuplicateStep); // ignored
            context.Validator.ReportMalicious(MaliciousMinerAddress, 100, Bytes.Empty, IReportingValidator.MaliciousCause.SiblingBlocksInSameStep); // ignored
            context.Validator.ReportMalicious(MaliciousMinerAddress, 100, Bytes.Empty, IReportingValidator.MaliciousCause.SiblingBlocksInSameStep); // ignored
            context.Validator.ReportBenign(TestItem.AddressC, 100, IReportingValidator.BenignCause.FutureBlock); // sent
            context.Validator.ReportBenign(TestItem.AddressC, 100, IReportingValidator.BenignCause.FutureBlock); // ignored
            context.Validator.ReportBenign(MaliciousMinerAddress, 101, IReportingValidator.BenignCause.FutureBlock); //sent
            context.Validator.ReportBenign(MaliciousMinerAddress, 101, IReportingValidator.BenignCause.IncorrectProposer); //ignored
            context.Validator.ReportBenign(MaliciousMinerAddress, 101, IReportingValidator.BenignCause.FutureBlock); //ignored
            context.Validator.ReportBenign(MaliciousMinerAddress, 101, IReportingValidator.BenignCause.IncorrectProposer); //ignored

            context.TxSender.Received(4).SendTransaction(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
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
                BlockHeader parentHeader = Build.A.BlockHeader.TestObject;
                IValidatorContract validatorContract = Substitute.For<IValidatorContract>();
                Address[] validators = initialValidators ?? new[] { MaliciousMinerAddress, NodeAddress };
                validatorContract.GetValidators(parentHeader).Returns(validators);

                ContractBasedValidator = new ContractBasedValidator(
                    validatorContract,
                    Substitute.For<IBlockTree>(),
                    Substitute.For<IReceiptFinder>(),
                    Substitute.For<IValidatorStore>(),
                    Substitute.For<IValidSealerStrategy>(),
                    Substitute.For<IAuRaBlockFinalizationManager>(),
                    parentHeader,
                    LimboLogs.Instance,
                    0,
                    PosdaoTransition,
                    forSealing);

                ContractBasedValidator.Validators ??= validators;

                ReportingValidatorContract = Substitute.For<IReportingValidatorContract>();
                ReportingValidatorContract.NodeAddress.Returns(NodeAddress);

                TxSender = Substitute.For<ITxSender>();
                ITxPool txPool = Substitute.For<ITxPool>();
                IWorldState stateProvider = Substitute.For<IWorldState>();
                ISpecProvider specProvider = Substitute.For<ISpecProvider>();
                stateProvider.GetNonce(ReportingValidatorContract.NodeAddress).Returns(UInt256.One);

                Validator = new ReportingContractBasedValidator(
                    ContractBasedValidator,
                    ReportingValidatorContract,
                    PosdaoTransition,
                    TxSender,
                    txPool,
                    new BlocksConfig(),
                    stateProvider,
                    cache ?? new ReportingContractBasedValidator.Cache(),
                    specProvider,
                    Substitute.For<IGasPriceOracle>(),
                    LimboLogs.Instance);
            }
        }
    }
}
