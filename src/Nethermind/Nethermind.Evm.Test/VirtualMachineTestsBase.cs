/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class VirtualMachineTestsBase
    {
        private IEthereumEcdsa _ethereumEcdsa;
        private ITransactionProcessor _processor;
        private ISnapshotableDb _stateDb;

        protected IVirtualMachine Machine { get; private set; }
        protected IStateProvider TestState { get; private set; }
        protected IStorageProvider Storage { get; private set; }

        protected static Address Contract { get; private set; } = new Address("0xd75a3a95360e44a3874e691fb48d77855f127069");
        protected static Address Sender { get; private set; } = TestItem.AddressA;
        protected static Address Recipient { get; private set; } = TestItem.AddressB;
        protected static Address Miner { get; private set; } = TestItem.AddressD;

        protected virtual long BlockNumber => MainNetSpecProvider.ByzantiumBlockNumber;
        protected virtual ISpecProvider SpecProvider => MainNetSpecProvider.Instance;
        protected IReleaseSpec Spec => SpecProvider.GetSpec(BlockNumber);

        [SetUp]
        public virtual void Setup()
        {
            ILogManager logger = LimboLogs.Instance;;
            IDb codeDb = new StateDb();
            _stateDb = new StateDb();
            TestState = new StateProvider(_stateDb, codeDb, logger);
            Storage = new StorageProvider(_stateDb, TestState, logger);
            _ethereumEcdsa = new EthereumEcdsa(SpecProvider, logger);
            IBlockhashProvider blockhashProvider = new TestBlockhashProvider();
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

        protected (ParityLikeTxTrace trace, Block block, Transaction tx) ExecuteInitAndTraceParityCall(params byte[] code)
        {
            (var block, var transaction) = PrepareInitTx(BlockNumber, 100000, code);
            ParityLikeTxTracer tracer = new ParityLikeTxTracer(block, transaction, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(transaction, block.Header, tracer);
            return (tracer.BuildResult(), block, transaction);
        }

        protected (ParityLikeTxTrace trace, Block block, Transaction tx) ExecuteAndTraceParityCall(params byte[] code)
        {
            (var block, var transaction) = PrepareTx(BlockNumber, 100000, code);
            ParityLikeTxTracer tracer = new ParityLikeTxTracer(block, transaction, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff | ParityTraceTypes.VmTrace);
            _processor.Execute(transaction, block.Header, tracer);
            return (tracer.BuildResult(), block, transaction);
        }
        
        protected (ParityLikeTxTrace trace, Block block, Transaction tx) ExecuteAndTraceParityCall(ParityTraceTypes traceTypes, params byte[] code)
        {
            (var block, var transaction) = PrepareTx(BlockNumber, 100000, code);
            ParityLikeTxTracer tracer = new ParityLikeTxTracer(block, transaction, traceTypes);
            _processor.Execute(transaction, block.Header, tracer);
            return (tracer.BuildResult(), block, transaction);
        }

        protected (ParityLikeTxTrace trace, Block block, Transaction tx) ExecuteAndTraceParityCall(byte[] input, UInt256 value, params byte[] code)
        {
            (var block, var transaction) = PrepareTx(BlockNumber, 100000, code, input, value);
            ParityLikeTxTracer tracer = new ParityLikeTxTracer(block, transaction, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(transaction, block.Header, tracer);
            return (tracer.BuildResult(), block, transaction);
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

        private (Block block, Transaction transaction) PrepareTx(long blockNumber, long gasLimit, byte[] code)
        {
            TestState.CreateAccount(Sender, 100.Ether());
            TestState.CreateAccount(Recipient, 100.Ether());
            Keccak codeHash = TestState.UpdateCode(code);
            TestState.UpdateCodeHash(TestItem.AddressB, codeHash, SpecProvider.GenesisSpec);

            TestState.Commit(SpecProvider.GenesisSpec);

            Transaction transaction = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithGasPrice(1)
                .To(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, blockNumber)
                .TestObject;

            Block block = BuildBlock(blockNumber);
            return (block, transaction);
        }

        private (Block block, Transaction transaction) PrepareTx(long blockNumber, long gasLimit, byte[] code, byte[] input, UInt256 value)
        {
            TestState.CreateAccount(Sender, 100.Ether());
            TestState.CreateAccount(Recipient, 100.Ether());
            Keccak codeHash = TestState.UpdateCode(code);
            TestState.UpdateCodeHash(TestItem.AddressB, codeHash, SpecProvider.GenesisSpec);

            TestState.Commit(SpecProvider.GenesisSpec);

            Transaction transaction = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithGasPrice(1)
                .WithData(input)
                .WithValue(value)
                .To(TestItem.AddressB)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, blockNumber)
                .TestObject;

            Block block = BuildBlock(blockNumber);
            return (block, transaction);
        }

        private (Block block, Transaction transaction) PrepareInitTx(long blockNumber, long gasLimit, byte[] code)
        {
            TestState.CreateAccount(Sender, 100.Ether());
            TestState.Commit(SpecProvider.GenesisSpec);

            Transaction transaction = Build.A.Transaction
                .WithTo(null)
                .WithData(Bytes.Empty)
                .WithGasLimit(gasLimit)
                .WithGasPrice(1)
                .WithInit(code)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, blockNumber)
                .TestObject;

            Block block = BuildBlock(blockNumber);
            return (block, transaction);
        }

        protected virtual Block BuildBlock(long blockNumber)
        {
            return Build.A.Block.WithNumber(blockNumber).WithGasLimit(8000000).WithBeneficiary(Miner).TestObject;
        }

        protected void AssertGas(CallOutputTracer receipt, long gas)
        {
            Assert.AreEqual(gas, receipt.GasSpent, "gas");
        }

        protected void AssertStorage(UInt256 address, Address value)
        {
            Assert.AreEqual(value.Bytes.PadLeft(32), Storage.Get(new StorageAddress(Recipient, address)).PadLeft(32), "storage");
        }

        protected void AssertStorage(UInt256 address, Keccak value)
        {
            Assert.AreEqual(value.Bytes, Storage.Get(new StorageAddress(Recipient, address)).PadLeft(32), "storage");
        }

        protected void AssertStorage(UInt256 address, byte[] value)
        {
            Assert.AreEqual(value.PadLeft(32), Storage.Get(new StorageAddress(Recipient, address)).PadLeft(32), "storage");
        }

        protected void AssertStorage(UInt256 address, BigInteger expectedValue)
        {
            byte[] actualValue = Storage.Get(new StorageAddress(Recipient, address));
            Assert.AreEqual(expectedValue.ToBigEndianByteArray(), actualValue, "storage");
        }
        
        protected void AssertStorage(StorageAddress storageAddress, BigInteger expectedValue)
        {
            byte[] actualValue = Storage.Get(storageAddress);
            Assert.AreEqual(expectedValue.ToBigEndianByteArray(), actualValue, $"storage {storageAddress}");
        }

        protected void AssertCodeHash(Address address, Keccak codeHash)
        {
            Assert.AreEqual(codeHash, TestState.GetCodeHash(address), "code hash");
        }

        protected class Prepare
        {
            private readonly List<byte> _byteCode = new List<byte>();
            public static Prepare EvmCode => new Prepare();
            public byte[] Done => _byteCode.ToArray();

            public Prepare Op(Instruction instruction)
            {
                _byteCode.Add((byte) instruction);
                return this;
            }

            public Prepare Create(byte[] code, BigInteger value)
            {
                StoreDataInMemory(0, code);
                PushData(code.Length);
                PushData(0);
                PushData(value);
                Op(Instruction.CREATE);
                return this;
            }

            public Prepare Create2(byte[] code, byte[] salt, BigInteger value)
            {
                StoreDataInMemory(0, code);
                PushData(salt);
                PushData(code.Length);
                PushData(0);
                PushData(value);
                Op(Instruction.CREATE2);
                return this;
            }

            public Prepare ForInitOf(byte[] codeToBeDeployed)
            {
                StoreDataInMemory(0, codeToBeDeployed.PadRight(32));
                PushData(codeToBeDeployed.Length);
                PushData(0);
                Op(Instruction.RETURN);

                return this;
            }

            public Prepare Call(Address address, long gasLimit)
            {
                PushData(0);
                PushData(0);
                PushData(0);
                PushData(0);
                PushData(0);
                PushData(address);
                PushData(gasLimit);
                Op(Instruction.CALL);
                return this;
            }
            
            public Prepare CallWithInput(Address address, long gasLimit, string input)
            {
                return CallWithInput(address, gasLimit, Bytes.FromHexString(input));
            }
            
            public Prepare CallWithInput(Address address, long gasLimit, byte[] input)
            {
                StoreDataInMemory(0, input);
                PushData(0);
                PushData(0);
                PushData(input.Length);
                PushData(0);
                PushData(0);
                PushData(address);
                PushData(gasLimit);
                Op(Instruction.CALL);
                return this;
            }
            
            public Prepare CallWithValue(Address address, long gasLimit, UInt256 value)
            {
                PushData(0);
                PushData(0);
                PushData(0);
                PushData(0);
                PushData(value);
                PushData(address);
                PushData(gasLimit);
                Op(Instruction.CALL);
                return this;
            }
            
            public Prepare DelegateCall(Address address, long gasLimit)
            {
                PushData(0);
                PushData(0);
                PushData(0);
                PushData(0);
                PushData(address);
                PushData(gasLimit);
                Op(Instruction.DELEGATECALL);
                return this;
            }
            
            public Prepare CallCode(Address address, long gasLimit)
            {
                PushData(0);
                PushData(0);
                PushData(0);
                PushData(0);
                PushData(0);
                PushData(address);
                PushData(gasLimit);
                Op(Instruction.CALLCODE);
                return this;
            }

            public Prepare StaticCall(Address address, long gasLimit)
            {                
                PushData(0);
                PushData(0);
                PushData(0);
                PushData(0);
                PushData(address);
                PushData(gasLimit);
                Op(Instruction.STATICCALL);
                return this;
            }

            public Prepare PushData(Address address)
            {
                PushData(address.Bytes);
                return this;
            }

            public Prepare PushData(BigInteger data)
            {
                PushData(data.ToBigEndianByteArray());
                return this;
            }

            public Prepare PushData(string data)
            {
                PushData(Bytes.FromHexString(data));
                return this;
            }

            public Prepare PushData(byte[] data)
            {
                _byteCode.Add((byte) (Instruction.PUSH1 + (byte) data.Length - 1));
                _byteCode.AddRange(data);
                return this;
            }

            public Prepare PushData(byte data)
            {
                PushData(new[] {data});
                return this;
            }

            public Prepare Data(string data)
            {
                _byteCode.AddRange(Bytes.FromHexString(data));
                return this;
            }

            public Prepare Data(byte[] data)
            {
                _byteCode.AddRange(data);
                return this;
            }

            public Prepare Data(byte data)
            {
                _byteCode.Add(data);
                return this;
            }

            public Prepare PersistData(string key, string value)
            {
                PushData(value);
                PushData(key);
                Op(Instruction.SSTORE);
                return this;
            }

            public Prepare StoreDataInMemory(int position, string hexString)
            {
                return StoreDataInMemory(position, Bytes.FromHexString(hexString));
            }

            public Prepare StoreDataInMemory(int position, byte[] data)
            {
                if (position % 32 != 0)
                {
                    throw new NotSupportedException();
                }

                for (int i = 0; i < data.Length; i += 32)
                {
                    PushData(data.Slice(i, data.Length - i).PadRight(32));
                    PushData(position + i);
                    Op(Instruction.MSTORE);
                }

                return this;
            }
        }
    }
}