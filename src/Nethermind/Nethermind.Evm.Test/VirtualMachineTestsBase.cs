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
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.BeamSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class VirtualMachineTestsBase
    {
        protected const string SampleHexData1 = "a01234";
        protected const string SampleHexData2 = "b15678";
        protected const string HexZero = "00";

        private IEthereumEcdsa _ethereumEcdsa;
        protected ITransactionProcessor _processor;
        private IDb _stateDb;
        protected bool UseBeamSync { get; set; }

        protected VirtualMachine Machine { get; private set; }
        protected IStateProvider TestState { get; private set; }
        protected IStorageProvider Storage { get; private set; }

        protected static Address Contract { get; } = new("0xd75a3a95360e44a3874e691fb48d77855f127069");
        protected static Address Sender { get; } = TestItem.AddressA;
        protected static Address Recipient { get; } = TestItem.AddressB;
        protected static Address Miner { get; } = TestItem.AddressD;

        protected static PrivateKey SenderKey { get; } = TestItem.PrivateKeyA;
        protected static PrivateKey RecipientKey { get; } = TestItem.PrivateKeyB;
        protected static PrivateKey MinerKey { get; } = TestItem.PrivateKeyD;

        protected virtual long BlockNumber => MainnetSpecProvider.ByzantiumBlockNumber;
        protected virtual ISpecProvider SpecProvider => MainnetSpecProvider.Instance;
        protected IReleaseSpec Spec => SpecProvider.GetSpec(BlockNumber);

        protected virtual ILogManager GetLogManager()
        {
            return LimboLogs.Instance;
        }
        
        [SetUp]
        public virtual void Setup()
        {
            ILogManager logManager = GetLogManager();

            MemDb beamStateDb = new();
            IDb beamSyncDb = new BeamSyncDb(new MemDb(), beamStateDb, StaticSelector.Full, logManager);
            IDb beamSyncCodeDb = new BeamSyncDb(new MemDb(), beamStateDb, StaticSelector.Full, logManager);
            IDb codeDb = UseBeamSync ? beamSyncCodeDb : new MemDb();
            _stateDb = UseBeamSync ? beamSyncDb : new MemDb();
            ITrieStore trieStore = new TrieStore(_stateDb, logManager);
            TestState = new StateProvider(trieStore, codeDb, logManager);
            Storage = new StorageProvider(trieStore, TestState, logManager);
            _ethereumEcdsa = new EthereumEcdsa(SpecProvider.ChainId, logManager);
            IBlockhashProvider blockhashProvider = TestBlockhashProvider.Instance;
            Machine = new VirtualMachine(TestState, Storage, blockhashProvider, SpecProvider, logManager);
            _processor = new TransactionProcessor(SpecProvider, TestState, Storage, Machine, logManager);
        }

        protected GethLikeTxTrace ExecuteAndTrace(params byte[] code)
        {
            GethLikeTxTracer tracer = new(GethTraceOptions.Default);
            (var block, var transaction) = PrepareTx(BlockNumber, 100000, code);
            _processor.Execute(transaction, block.Header, tracer);
            return tracer.BuildResult();
        }

        protected GethLikeTxTrace ExecuteAndTrace(long blockNumber, long gasLimit, params byte[] code)
        {
            GethLikeTxTracer tracer = new(GethTraceOptions.Default);
            (var block, var transaction) = PrepareTx(blockNumber, gasLimit, code);
            _processor.Execute(transaction, block.Header, tracer);
            return tracer.BuildResult();
        }

        protected TestAllTracerWithOutput Execute(params byte[] code)
        {
            (var block, var transaction) = PrepareTx(BlockNumber, 100000, code);
            TestAllTracerWithOutput tracer = CreateTracer();
            _processor.Execute(transaction, block.Header, tracer);
            return tracer;
        }

        protected virtual TestAllTracerWithOutput CreateTracer() => new();

        protected T Execute<T>(T tracer, params byte[] code) where T : ITxTracer
        {
            (var block, var transaction) = PrepareTx(BlockNumber, 100000, code);
            _processor.Execute(transaction, block.Header, tracer);
            return tracer;
        }

        protected TestAllTracerWithOutput Execute(long blockNumber, long gasLimit, byte[] code)
        {
            (Block block, Transaction transaction) = PrepareTx(blockNumber, gasLimit, code);
            TestAllTracerWithOutput tracer = CreateTracer();
            _processor.Execute(transaction, block.Header, tracer);
            return tracer;
        }

        protected (Block block, Transaction transaction) PrepareTx(
            long blockNumber,
            long gasLimit,
            byte[] code,
            SenderRecipientAndMiner senderRecipientAndMiner = null)
        {
            senderRecipientAndMiner ??= SenderRecipientAndMiner.Default;
            TestState.CreateAccount(senderRecipientAndMiner.Sender, 100.Ether());
            TestState.CreateAccount(senderRecipientAndMiner.Recipient, 100.Ether());
            Keccak codeHash = TestState.UpdateCode(code);
            TestState.UpdateCodeHash(senderRecipientAndMiner.Recipient, codeHash, SpecProvider.GenesisSpec);

            GetLogManager().GetClassLogger().Debug("Committing initial state");
            TestState.Commit(SpecProvider.GenesisSpec);
            GetLogManager().GetClassLogger().Debug("Committed initial state");
            GetLogManager().GetClassLogger().Debug("Committing initial tree");
            TestState.CommitTree(0);
            GetLogManager().GetClassLogger().Debug("Committed initial tree");

            Transaction transaction = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithGasPrice(1)
                .To(senderRecipientAndMiner.Recipient)
                .SignedAndResolved(_ethereumEcdsa, senderRecipientAndMiner.SenderKey)
                .TestObject;

            Block block = BuildBlock(blockNumber, senderRecipientAndMiner, transaction);
            return (block, transaction);
        }

        protected (Block block, Transaction transaction) PrepareTx(long blockNumber, long gasLimit, byte[] code, byte[] input, UInt256 value, SenderRecipientAndMiner senderRecipientAndMiner = null)
        {
            senderRecipientAndMiner ??= SenderRecipientAndMiner.Default;
            TestState.CreateAccount(senderRecipientAndMiner.Sender, 100.Ether());
            TestState.CreateAccount(senderRecipientAndMiner.Recipient, 100.Ether());
            Keccak codeHash = TestState.UpdateCode(code);
            TestState.UpdateCodeHash(senderRecipientAndMiner.Recipient, codeHash, SpecProvider.GenesisSpec);

            TestState.Commit(SpecProvider.GenesisSpec);

            Transaction transaction = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithGasPrice(1)
                .WithData(input)
                .WithValue(value)
                .To(senderRecipientAndMiner.Recipient)
                .SignedAndResolved(_ethereumEcdsa, senderRecipientAndMiner.SenderKey)
                .TestObject;

            Block block = BuildBlock(blockNumber, senderRecipientAndMiner);
            return (block, transaction);
        }

        protected (Block block, Transaction transaction) PrepareInitTx(long blockNumber, long gasLimit, byte[] code, SenderRecipientAndMiner senderRecipientAndMiner = null)
        {
            senderRecipientAndMiner ??= SenderRecipientAndMiner.Default;
            TestState.CreateAccount(senderRecipientAndMiner.Sender, 100.Ether());
            TestState.Commit(SpecProvider.GenesisSpec);

            Transaction transaction = Build.A.Transaction
                .WithTo(null)
                .WithGasLimit(gasLimit)
                .WithGasPrice(1)
                .WithCode(code)
                .SignedAndResolved(_ethereumEcdsa, senderRecipientAndMiner.SenderKey)
                .TestObject;

            Block block = BuildBlock(blockNumber, senderRecipientAndMiner);
            return (block, transaction);
        }

        protected Block BuildBlock(long blockNumber, SenderRecipientAndMiner senderRecipientAndMiner)
        {
            return BuildBlock(blockNumber, senderRecipientAndMiner, null);
        }

        protected virtual Block BuildBlock(long blockNumber, SenderRecipientAndMiner senderRecipientAndMiner, Transaction tx)
        {
            senderRecipientAndMiner ??= SenderRecipientAndMiner.Default;
            return Build.A.Block.WithNumber(blockNumber).WithTransactions(tx == null ? new Transaction[0] : new[] {tx}).WithGasLimit(8000000).WithBeneficiary(senderRecipientAndMiner.Miner).TestObject;
        }

        protected void AssertGas(TestAllTracerWithOutput receipt, long gas)
        {
            Assert.AreEqual(gas, receipt.GasSpent, "gas");
        }

        protected void AssertStorage(UInt256 address, Address value)
        {
            Assert.AreEqual(value.Bytes.PadLeft(32), Storage.Get(new StorageCell(Recipient, address)).PadLeft(32), "storage");
        }

        protected void AssertStorage(UInt256 address, Keccak value)
        {
            Assert.AreEqual(value.Bytes, Storage.Get(new StorageCell(Recipient, address)).PadLeft(32), "storage");
        }

        protected void AssertStorage(UInt256 address, ReadOnlySpan<byte> value)
        {
            Assert.AreEqual(new ZeroPaddedSpan(value, 32 - value.Length, PadDirection.Left).ToArray(), Storage.Get(new StorageCell(Recipient, address)).PadLeft(32), "storage");
        }

        protected void AssertStorage(UInt256 address, BigInteger expectedValue)
        {
            byte[] actualValue = Storage.Get(new StorageCell(Recipient, address));
            byte[] expected = expectedValue < 0 ? expectedValue.ToBigEndianByteArray(32) : expectedValue.ToBigEndianByteArray();  
            Assert.AreEqual(expected, actualValue, "storage");
        }
        
        protected void AssertStorage(UInt256 address, UInt256 expectedValue)
        {
            byte[] bytes = ((BigInteger)expectedValue).ToBigEndianByteArray();

            byte[] actualValue = Storage.Get(new StorageCell(Recipient, address));
            Assert.AreEqual(bytes, actualValue, "storage");
        }

        private static int _callIndex = -1;
        
        protected void AssertStorage(StorageCell storageCell, UInt256 expectedValue)
        {
            _callIndex++;
            if (!TestState.AccountExists(storageCell.Address))
            {
                Assert.AreEqual(expectedValue.ToBigEndian().WithoutLeadingZeros().ToArray(), new byte[] {0}, $"storage {storageCell}, call {_callIndex}");
            }
            else
            {
                byte[] actualValue = Storage.Get(storageCell);
                Assert.AreEqual(expectedValue.ToBigEndian().WithoutLeadingZeros().ToArray(), actualValue, $"storage {storageCell}, call {_callIndex}");    
            }
        }

        protected void AssertCodeHash(Address address, Keccak codeHash)
        {
            Assert.AreEqual(codeHash, TestState.GetCodeHash(address), "code hash");
        }
    }
}
