// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    [TestFixture(true)]
    [TestFixture(false)]
    [Parallelizable(ParallelScope.All)]
    public class GasEstimationTests
    {
        private readonly ExecutionType _executionType;

        public GasEstimationTests(bool useCreates)
        {
            _executionType = useCreates ? ExecutionType.Create : ExecutionType.Call;
        }

        [Test]
        public void Does_not_take_into_account_precompiles()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(1000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.Transaction, false);
            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.Call, true);
            testEnvironment.tracer.ReportActionEnd(400,
                Array.Empty<byte>()); // this would not happen but we want to ensure that precompiles are ignored
            testEnvironment.tracer.ReportActionEnd(600, Array.Empty<byte>());

            testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer).Should().Be(0);
        }

        [Test]
        public void Only_traces_actions_and_receipts()
        {
            EstimateGasTracer tracer = new();
            (tracer.IsTracingActions && tracer.IsTracingReceipt).Should().BeTrue();
            (tracer.IsTracingBlockHash
             || tracer.IsTracingState
             || tracer.IsTracingStorage
             || tracer.IsTracingCode
             || tracer.IsTracingInstructions
             || tracer.IsTracingMemory
             || tracer.IsTracingStack
             || tracer.IsTracingOpLevelStorage).Should().BeFalse();
        }

        [Test]
        public void Handles_well_top_level()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(1000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.Transaction, false);
            testEnvironment.tracer.ReportActionEnd(600, Array.Empty<byte>());

            testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer).Should().Be(0);
        }

        [Test]
        public void Handles_well_serial_calls()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(1000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.Transaction, false);
            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                _executionType, false);
            testEnvironment.tracer.ReportActionEnd(400, Array.Empty<byte>());
            testEnvironment.tracer.ReportAction(400, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType,
                false);
            if (_executionType.IsAnyCreate())
            {
                testEnvironment.tracer.ReportActionEnd(200, Address.Zero, Array.Empty<byte>());
                testEnvironment.tracer.ReportActionEnd(300, Array.Empty<byte>());
            }
            else
            {
                testEnvironment.tracer.ReportActionEnd(200, Array.Empty<byte>());
                testEnvironment.tracer.ReportActionEnd(300, Array.Empty<byte>()); // should not happen
            }

            testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer).Should().Be(14L);
        }

        [Test]
        public void Handles_well_errors()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(1000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.Transaction, false);
            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                _executionType, false);
            testEnvironment.tracer.ReportAction(400, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType,
                false);

            if (_executionType.IsAnyCreate())
            {
                testEnvironment.tracer.ReportActionError(EvmExceptionType.Other);
                testEnvironment.tracer.ReportActionEnd(400, Address.Zero, Array.Empty<byte>());
                testEnvironment.tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }
            else
            {
                testEnvironment.tracer.ReportActionError(EvmExceptionType.Other);
                testEnvironment.tracer.ReportActionEnd(400, Array.Empty<byte>());
                testEnvironment.tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }

            testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer).Should().Be(24L);
        }

        [Test]
        public void Handles_well_revert()
        {
            TestEnvironment testEnvironment = new();
            long gasLimit = 100_000_000;
            Transaction tx = Build.A.Transaction.WithGasLimit(gasLimit).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

            long gasLeft = gasLimit - 22000;
            testEnvironment.tracer.ReportAction(gasLeft, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.Transaction, false);
            gasLeft = 63 * gasLeft / 64;
            testEnvironment.tracer.ReportAction(gasLeft, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                _executionType, false);
            gasLeft = 63 * gasLeft / 64;
            testEnvironment.tracer.ReportAction(gasLeft, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                _executionType, false);

            if (_executionType.IsAnyCreate())
            {
                testEnvironment.tracer.ReportActionError(EvmExceptionType.Revert, 96000000);
                testEnvironment.tracer.ReportActionError(EvmExceptionType.Revert, 98000000);
                testEnvironment.tracer.ReportActionError(EvmExceptionType.Revert, 99000000);
            }
            else
            {
                testEnvironment.tracer.ReportActionError(EvmExceptionType.Revert, 96000000);
                testEnvironment.tracer.ReportActionError(EvmExceptionType.Revert, 98000000);
                testEnvironment.tracer.ReportActionError(EvmExceptionType.Revert, 99000000);
            }

            testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer).Should().Be(35146L);
        }

        [Test]
        public void Easy_one_level_case()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(128).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(128, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.Transaction, false);
            testEnvironment.tracer.ReportAction(100, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType,
                false);

            testEnvironment.tracer.ReportActionEnd(63, Array.Empty<byte>()); // second level
            testEnvironment.tracer.ReportActionEnd(65, Array.Empty<byte>());

            testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer).Should().Be(1);
        }

        [Test]
        public void Handles_well_nested_calls_where_most_nested_defines_excess()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(1000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.Transaction, false);
            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                _executionType, false);
            testEnvironment.tracer.ReportAction(400, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType,
                false);

            if (_executionType.IsAnyCreate())
            {
                testEnvironment.tracer.ReportActionEnd(200, Address.Zero, Array.Empty<byte>()); // second level
                testEnvironment.tracer.ReportActionEnd(400, Address.Zero, Array.Empty<byte>());
                testEnvironment.tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }
            else
            {
                testEnvironment.tracer.ReportActionEnd(200, Array.Empty<byte>()); // second level
                testEnvironment.tracer.ReportActionEnd(400, Array.Empty<byte>());
                testEnvironment.tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }

            testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer).Should().Be(18);
        }

        [Test]
        public void Handles_well_nested_calls_where_least_nested_defines_excess()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(1000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                ExecutionType.Transaction, false);
            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(),
                _executionType, false);
            testEnvironment.tracer.ReportAction(400, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType,
                false);

            if (_executionType.IsAnyCreate())
            {
                testEnvironment.tracer.ReportActionEnd(300, Address.Zero, Array.Empty<byte>()); // second level
                testEnvironment.tracer.ReportActionEnd(200, Address.Zero, Array.Empty<byte>());
                testEnvironment.tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }
            else
            {
                testEnvironment.tracer.ReportActionEnd(300, Array.Empty<byte>()); // second level
                testEnvironment.tracer.ReportActionEnd(200, Array.Empty<byte>());
                testEnvironment.tracer.ReportActionEnd(500, Array.Empty<byte>()); // should not happen
            }

            testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer).Should().Be(17);
        }

        private class TestEnvironment
        {
            public ISpecProvider _specProvider;
            public IEthereumEcdsa _ethereumEcdsa;
            public TransactionProcessor _transactionProcessor;
            public IWorldState _stateProvider;
            public EstimateGasTracer tracer;
            public GasEstimator estimator;

            public TestEnvironment()
            {
                _specProvider = MainnetSpecProvider.Instance;
                MemDb stateDb = new();
                TrieStore trieStore = new(stateDb, LimboLogs.Instance);
                _stateProvider = new WorldState(trieStore, new MemDb(), LimboLogs.Instance);
                _stateProvider.CreateAccount(TestItem.AddressA, 1.Ether());
                _stateProvider.Commit(_specProvider.GenesisSpec);
                _stateProvider.CommitTree(0);

                CodeInfoRepository codeInfoRepository = new();
                VirtualMachine virtualMachine = new(TestBlockhashProvider.Instance, _specProvider, codeInfoRepository, LimboLogs.Instance);
                _transactionProcessor = new TransactionProcessor(_specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
                _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId, LimboLogs.Instance);

                tracer = new();
                BlocksConfig blocksConfig = new();
                estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);
            }
        }
    }
}
