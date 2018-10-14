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
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class VirtualMachineTestsBase
    {
        private readonly IEthereumSigner _ethereumSigner;
        private readonly ITransactionProcessor _processor;
        private readonly ISnapshotableDb _stateDb;
        protected internal readonly ISpecProvider SpecProvider;
        protected internal IStateProvider TestState { get; }
        protected internal IStorageProvider Storage { get; }

        protected internal static Address Sender { get; } = TestObject.AddressA;
        protected internal static Address Recipient { get; } = TestObject.AddressB;

        protected virtual UInt256 BlockNumber => MainNetSpecProvider.ByzantiumBlockNumber;

        protected IReleaseSpec Spec => SpecProvider.GetSpec(BlockNumber);

        public VirtualMachineTestsBase()
        {
            SpecProvider = MainNetSpecProvider.Instance;
            ILogManager logger = NullLogManager.Instance;
            IDb codeDb = new StateDb();
            _stateDb = new StateDb();
            StateTree stateTree = new StateTree(_stateDb);
            TestState = new StateProvider(stateTree, codeDb, logger);
            Storage = new StorageProvider(_stateDb, TestState, logger);
            _ethereumSigner = new EthereumSigner(SpecProvider, logger);
            IBlockhashProvider blockhashProvider = new TestBlockhashProvider();
            IVirtualMachine virtualMachine = new VirtualMachine(TestState, Storage, blockhashProvider, logger);

            _processor = new TransactionProcessor(SpecProvider, TestState, Storage, virtualMachine, logger);
        }

        [SetUp]
        public virtual void Setup()
        {
            _stateDbSnapshot = _stateDb.TakeSnapshot();
            _stateRoot = TestState.StateRoot;
        }

        private int _stateDbSnapshot;
        private Keccak _stateRoot;

        [TearDown]
        public void TearDown()
        {
            Storage.Reset();
            TestState.Reset();
            TestState.StateRoot = _stateRoot;

            _stateDb.Restore(_stateDbSnapshot);
        }

        protected (TransactionReceipt Receipt, TransactionTrace Trace) ExecuteAndTrace(params byte[] code)
        {
            return Execute(BlockNumber, 100000, code, true);
        }
        
        protected (TransactionReceipt Receipt, TransactionTrace Trace) ExecuteAndTrace(UInt256 blockNumber, long gasLimit, params byte[] code)
        {
            return Execute(blockNumber, gasLimit, code, true);
        }
        
        protected TransactionReceipt Execute(params byte[] code)
        {
            return Execute(BlockNumber, 100000, code, false).Receipt;
        }

        protected TransactionReceipt Execute(UInt256 blockNumber, long gasLimit, byte[] code)
        {
            return Execute(blockNumber, gasLimit, code, false).Receipt;
        }
        
        private (TransactionReceipt Receipt, TransactionTrace Trace) Execute(UInt256 blockNumber, long gasLimit, byte[] code, bool shouldTrace)
        {
            TestState.CreateAccount(Sender, 100.Ether());
            TestState.CreateAccount(Recipient, 100.Ether());
            Keccak codeHash = TestState.UpdateCode(code);
            TestState.UpdateCodeHash(TestObject.AddressB, codeHash, SpecProvider.GenesisSpec);

            TestState.Commit(SpecProvider.GenesisSpec);

            Transaction transaction = Build.A.Transaction
                .WithGasLimit((ulong) gasLimit)
                .WithGasPrice(1)
                .WithTo(TestObject.AddressB)
                .SignedAndResolved(_ethereumSigner, TestObject.PrivateKeyA, blockNumber)
                .TestObject;

            Block block = Build.A.Block.WithNumber(blockNumber).TestObject;
            return _processor.Execute(0, transaction, block.Header, shouldTrace);
        }

        protected void AssertGas(TransactionReceipt receipt, long gas)
        {
            Assert.AreEqual(gas, receipt.GasUsed, "gas");
        }

        protected void AssertStorage(UInt256 address, Keccak value)
        {
            Assert.AreEqual(value.Bytes, Storage.Get(new StorageAddress(Recipient, address)).PadLeft(32), "storage");
        }

        protected void AssertStorage(UInt256 address, byte[] value)
        {
            Assert.AreEqual(value.PadLeft(32), Storage.Get(new StorageAddress(Recipient, address)).PadLeft(32), "storage");
        }

        protected void AssertStorage(UInt256 address, BigInteger value)
        {
            Assert.AreEqual(value.ToBigEndianByteArray(), Storage.Get(new StorageAddress(Recipient, address)), "storage");
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
            
            public Prepare StoreDataInMemory(int poisition, byte[] data)
            {
                if (poisition % 32 != 0)
                {
                    throw new NotSupportedException();
                }
                
                for (int i = 0; i < data.Length; i += 32)
                {
                    PushData(data.Slice(i, data.Length - i).PadRight(32));
                    PushData(i);
                    Op(Instruction.MSTORE);    
                }
                
                return this;
            }
        }
    }
}