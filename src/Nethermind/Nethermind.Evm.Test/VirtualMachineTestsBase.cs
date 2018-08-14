using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class VirtualMachineTestsBase : ITransactionTracer
    {
        private readonly ConsoleTransactionTracer _tracer = new ConsoleTransactionTracer(new UnforgivingJsonSerializer());
        private readonly IEthereumSigner _ethereumSigner;
        private readonly ITransactionProcessor _processor;
        private readonly ISnapshotableDb _stateDb;
        private readonly IDbProvider _storageDbProvider;
        protected internal readonly ISpecProvider SpecProvider;
        protected internal IStateProvider TestState { get; }
        protected internal IStorageProvider Storage { get; }

        protected internal static Address A { get; } = TestObject.AddressA;
        protected internal static Address B { get; } = TestObject.AddressB;

        protected virtual int BlockNumber => 10000;

        protected IReleaseSpec Spec => SpecProvider.GetSpec(BlockNumber);

        public VirtualMachineTestsBase()
        {
            SpecProvider = RopstenSpecProvider.Instance;
            ILogManager logger = NullLogManager.Instance;
            IDb codeDb = new MemDb();
            _stateDb = new SnapshotableDb(new MemDb());
            StateTree stateTree = new StateTree(_stateDb);
            TestState = new StateProvider(stateTree, codeDb, logger);
            _storageDbProvider = new MemDbProvider(logger);
            Storage = new StorageProvider(_storageDbProvider, TestState, logger);
            _ethereumSigner = new EthereumSigner(SpecProvider, logger);
            IBlockhashProvider blockhashProvider = new TestBlockhashProvider();
            IVirtualMachine virtualMachine = new VirtualMachine(TestState, Storage, blockhashProvider, logger);
            
            _processor = new TransactionProcessor(SpecProvider, TestState, Storage, virtualMachine, this, logger);
        }

        protected TransactionTrace TransactionTrace { get; private set; }

        public bool IsTracingEnabled
        {
            get => _tracer.IsTracingEnabled;
            protected set => _tracer.IsTracingEnabled = value;
        }
        public void SaveTrace(Keccak hash, TransactionTrace trace)
        {
            TransactionTrace = trace;
            _tracer.SaveTrace(hash, trace);
        }
        
        [SetUp]
        public void Setup()
        {
            _tracer.IsTracingEnabled = false;
            TransactionTrace = null;

            _stateDbSnapshot = _stateDb.TakeSnapshot();
            _storageDbSnapshot = _storageDbProvider.TakeSnapshot();
            _stateRoot = TestState.StateRoot;
        }

        private int _stateDbSnapshot;
        private int _storageDbSnapshot;
        private Keccak _stateRoot;

        [TearDown]
        public void TearDown()
        {
            Storage.ClearCaches();
            TestState.Reset();
            TestState.StateRoot = _stateRoot;

            _storageDbProvider.Restore(_storageDbSnapshot);
            _stateDb.Restore(_stateDbSnapshot);
        }
        
        protected TransactionReceipt Execute(params byte[] code)
        {
            return Execute(BlockNumber, 100000, code);
        }
        
        protected TransactionReceipt Execute(BigInteger blockNumber, long gasLimit, byte[] code)
        {
            TestState.CreateAccount(A, 100.Ether());
            TestState.CreateAccount(B, 100.Ether());
            Keccak codeHash = TestState.UpdateCode(code);
            TestState.UpdateCodeHash(TestObject.AddressB, codeHash, SpecProvider.GenesisSpec);

            TestState.Commit(SpecProvider.GenesisSpec);

            Transaction transaction = Build.A.Transaction
                .WithGasLimit(gasLimit)
                .WithGasPrice(1)
                .WithTo(TestObject.AddressB)
                .SignedAndResolved(_ethereumSigner, TestObject.PrivateKeyA, blockNumber)
                .TestObject;

            Block block = Build.A.Block.WithNumber(blockNumber).TestObject;
            TransactionReceipt receipt = _processor.Execute(transaction, block.Header);
            return receipt;
        }
        
        protected void AssertGas(TransactionReceipt receipt, long gas)
        {
            Assert.AreEqual(gas, receipt.GasUsed, "gas");
        }

        protected void AssertStorage(BigInteger address, Keccak value)
        {
            Assert.AreEqual(value.Bytes, Storage.Get(new StorageAddress(B, address)).PadLeft(32), "storage");
        }
        
        protected void AssertStorage(BigInteger address, Hex value)
        {
            Assert.AreEqual(((byte[])value).PadLeft(32), Storage.Get(new StorageAddress(B, address)).PadLeft(32), "storage");
        }
        
        protected void AssertStorage(BigInteger address, BigInteger value)
        {
            Assert.AreEqual(value.ToBigEndianByteArray(), Storage.Get(new StorageAddress(B, address)), "storage");
        }
        
        protected class Prepare
        {
            private List<byte> _byteCode = new List<byte>();
            public static Prepare EvmCode => new Prepare();
            public byte[] Done => _byteCode.ToArray();

            public Prepare Op(Instruction instruction)
            {
                _byteCode.Add((byte)instruction);
                return this;
            }

            public Prepare Data(Hex data)
            {
                _byteCode.AddRange((byte[])data);
                return this;
            }

            public Prepare Create(byte[] code, BigInteger value)
            {
                // push code and store in memory
                for (int i = 0; i < code.Length; i+= 32)
                {
                    PushData(code.Slice(i, Math.Min(32, code.Length - i)).PadRight(32));
                    PushData(i);
                    Op(Instruction.MSTORE);   
                }
                
                PushData(code.Length);
                PushData(0);
                PushData(value);
                Op(Instruction.CREATE);
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
                PushData((byte[])address.Hex);
                return this;
            }

            public Prepare PushData(BigInteger data)
            {
                PushData(data.ToBigEndianByteArray());
                return this;
            }
            
            public Prepare PushData(string data)
            {
                PushData((byte[])new Hex(data));
                return this;
            }

            public Prepare PushData(byte[] data)
            {
                _byteCode.Add((byte)(Instruction.PUSH1 + (byte)data.Length - 1));
                _byteCode.AddRange(data);
                return this;
            }

            public Prepare PushData(byte data)
            {
                PushData(new [] {data});
                return this;
            }

            public Prepare Data(string data)
            {
                _byteCode.AddRange((byte[])new Hex(data));
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
        }
    }
}