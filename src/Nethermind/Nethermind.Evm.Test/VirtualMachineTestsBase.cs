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
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.BeamSync;
using Nethermind.Synchronization.ParallelSync;
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
        private ISnapshotableDb _stateDb;
        protected bool UseBeamSync { get; set; }

        protected IVirtualMachine Machine { get; private set; }
        protected IStateProvider TestState { get; private set; }
        protected IStorageProvider Storage { get; private set; }

        protected static Address Contract { get; } = new Address("0xd75a3a95360e44a3874e691fb48d77855f127069");
        protected static Address Sender { get; } = TestItem.AddressA;
        protected static Address Recipient { get; } = TestItem.AddressB;
        protected static Address Miner { get; } = TestItem.AddressD;

        protected static PrivateKey SenderKey { get; } = TestItem.PrivateKeyA;
        protected static PrivateKey RecipientKey { get; } = TestItem.PrivateKeyB;
        protected static PrivateKey MinerKey { get; } = TestItem.PrivateKeyD;

        protected virtual long BlockNumber => MainnetSpecProvider.ByzantiumBlockNumber;
        protected virtual ISpecProvider SpecProvider => MainnetSpecProvider.Instance;
        protected IReleaseSpec Spec => SpecProvider.GetSpec(BlockNumber);

        [SetUp]
        public virtual void Setup()
        {
            ILogManager logger = LimboLogs.Instance;

            MemDb beamStateDb = new MemDb();
            ISnapshotableDb beamSyncDb = new StateDb(new BeamSyncDb(new MemDb(), beamStateDb, StaticSelector.Full, LimboLogs.Instance));
            IDb beamSyncCodeDb = new BeamSyncDb(new MemDb(), beamStateDb, StaticSelector.Full, LimboLogs.Instance);
            IDb codeDb = UseBeamSync ? beamSyncCodeDb : new StateDb();
            _stateDb = UseBeamSync ? beamSyncDb : new StateDb();
            TestState = new StateProvider(_stateDb, codeDb, logger);
            Storage = new StorageProvider(_stateDb, TestState, logger);
            _ethereumEcdsa = new EthereumEcdsa(SpecProvider.ChainId, logger);
            IBlockhashProvider blockhashProvider = TestBlockhashProvider.Instance;
            Machine = new VirtualMachine(TestState, Storage, blockhashProvider, SpecProvider, logger);
            _processor = new TransactionProcessor(SpecProvider, TestState, Storage, Machine, logger);
        }

        protected GethLikeTxTrace ExecuteAndTrace(params byte[] code)
        {
            GethLikeTxTracer tracer = new GethLikeTxTracer(GethTraceOptions.Default);
            (var block, var transaction) = PrepareTx(BlockNumber, 100000, code);
            _processor.Execute(transaction, block.Header, tracer);
            return tracer.BuildResult();
        }

        protected GethLikeTxTrace ExecuteAndTrace(long blockNumber, long gasLimit, params byte[] code)
        {
            GethLikeTxTracer tracer = new GethLikeTxTracer(GethTraceOptions.Default);
            (var block, var transaction) = PrepareTx(blockNumber, gasLimit, code);
            _processor.Execute(transaction, block.Header, tracer);
            return tracer.BuildResult();
        }

        protected CallOutputTracer Execute(params byte[] code)
        {
            (var block, var transaction) = PrepareTx(BlockNumber, 100000, code);
            CallOutputTracer tracer = new CallOutputTracer();
            _processor.Execute(transaction, block.Header, tracer);
            return tracer;
        }

        protected CallOutputTracer Execute(long blockNumber, long gasLimit, byte[] code)
        {
            (var block, var transaction) = PrepareTx(blockNumber, gasLimit, code);
            CallOutputTracer tracer = new CallOutputTracer();
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

            TestState.Commit(SpecProvider.GenesisSpec);
            TestState.CommitTree();

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
                .WithData(Array.Empty<byte>())
                .WithGasLimit(gasLimit)
                .WithGasPrice(1)
                .WithInit(code)
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

        protected void AssertGas(CallOutputTracer receipt, long gas)
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

        protected void AssertStorage(UInt256 address, byte[] value)
        {
            Assert.AreEqual(value.PadLeft(32), Storage.Get(new StorageCell(Recipient, address)).PadLeft(32), "storage");
        }

        protected void AssertStorage(UInt256 address, BigInteger expectedValue)
        {
            byte[] actualValue = Storage.Get(new StorageCell(Recipient, address));
            Assert.AreEqual(expectedValue.ToBigEndianByteArray(), actualValue, "storage");
        }

        private static int _callIndex = -1;
        
        protected void AssertStorage(StorageCell storageCell, BigInteger expectedValue)
        {
            _callIndex++;
            if (!TestState.AccountExists(storageCell.Address))
            {
                Assert.AreEqual(expectedValue.ToBigEndianByteArray(), new byte[1] {0}, $"storage {storageCell}, call {_callIndex}");
            }
            else
            {
                byte[] actualValue = Storage.Get(storageCell);
                Assert.AreEqual(expectedValue.ToBigEndianByteArray(), actualValue, $"storage {storageCell}, call {_callIndex}");    
            }
        }

        protected void AssertCodeHash(Address address, Keccak codeHash)
        {
            Assert.AreEqual(codeHash, TestState.GetCodeHash(address), "code hash");
        }
    }
}