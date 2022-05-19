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

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    [TestFixture(true)]
    [TestFixture(false)]
    [Parallelizable(ParallelScope.All)]
    public class VerkleGasEstimationTests
    {
        private readonly ExecutionType _executionType;
        
        public VerkleGasEstimationTests(bool useCreates)
        {
            _executionType = useCreates ? ExecutionType.Create : ExecutionType.Call;
        }

        [Test]
        public void Does_not_take_into_account_precompiles()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(1000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
            
            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Transaction, false);
            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Call, true);
            testEnvironment.tracer.ReportActionEnd(400, Array.Empty<byte>()); // this would not happen but we want to ensure that precompiles are ignored
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
            
            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Transaction, false);
            testEnvironment.tracer.ReportActionEnd(600, Array.Empty<byte>());
            
            testEnvironment.estimator.Estimate(tx, block.Header, testEnvironment.tracer).Should().Be(0);
        }

        [Test]
        public void Handles_well_serial_calls()
        {
            TestEnvironment testEnvironment = new();
            Transaction tx = Build.A.Transaction.WithGasLimit(1000).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
            
            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Transaction, false);
            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);
            testEnvironment.tracer.ReportActionEnd(400, Array.Empty<byte>());
            testEnvironment.tracer.ReportAction(400, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);
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
            
            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Transaction, false);
            testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);
            testEnvironment.tracer.ReportAction(400, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);
        
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
           long gasLimit = 100000000;
           Transaction tx = Build.A.Transaction.WithGasLimit(100000000).TestObject;
           Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
           
           long gasLeft = gasLimit - 22000;
           testEnvironment.tracer.ReportAction(gasLeft, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Transaction, false);
           gasLeft = 63 * gasLeft / 64;
           testEnvironment.tracer.ReportAction(gasLeft, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);
           gasLeft = 63 * gasLeft / 64;
           testEnvironment.tracer.ReportAction(gasLeft, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);
       
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
           
           testEnvironment.tracer.ReportAction(128, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Transaction, false);
           testEnvironment.tracer.ReportAction(100, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);
       
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
           
           testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Transaction, false);
           testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);
           testEnvironment.tracer.ReportAction(400, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);
       
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
           
           testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Transaction, false);
           testEnvironment.tracer.ReportAction(1000, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);
           testEnvironment.tracer.ReportAction(400, 0, Address.Zero, Address.Zero, Array.Empty<byte>(), _executionType, false);
       
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
           public IStateProvider _stateProvider;
           public EstimateGasTracer tracer;
           public GasEstimator estimator;

           public TestEnvironment()
           {
               _specProvider = MainnetSpecProvider.Instance;
               MemDb stateDb = new();
               VerkleTrieStore trieStore = new(DatabaseScheme.MemoryDb, LimboLogs.Instance);
               var stateProvider = new VerkleStateProvider(trieStore, LimboLogs.Instance, new MemDb());
               _stateProvider = stateProvider;
               _stateProvider.CreateAccount(TestItem.AddressA, 1.Ether());
               _stateProvider.Commit(_specProvider.GenesisSpec);
               _stateProvider.CommitTree(0);

               VerkleStorageProvider storageProvider = new(stateProvider, LimboLogs.Instance);
               VirtualMachine virtualMachine = new(TestBlockhashProvider.Instance, _specProvider, LimboLogs.Instance);
               _transactionProcessor = new TransactionProcessor(_specProvider, _stateProvider, storageProvider, virtualMachine, LimboLogs.Instance);
               _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId, LimboLogs.Instance);
               
               tracer = new();
               estimator = new(_transactionProcessor, _stateProvider, _specProvider);
           }
       }
    }
}
