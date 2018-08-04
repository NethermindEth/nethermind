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
        private readonly IEthereumSigner _ethereumSigner;
        private readonly ITransactionProcessor _processor;
        private readonly ISpecProvider _specProvider;
        private readonly ISnapshotableDb _stateDb;
        private readonly IDbProvider _storageDbProvider;
        private readonly IStateProvider _stateProvider;
        protected internal IStorageProvider Storage { get; }

        protected internal static Address A { get; } = TestObject.AddressA;
        protected internal static Address B { get; } = TestObject.AddressB;

        protected virtual int BlockNumber => 10000;

        public VirtualMachineTestsBase()
        {
            _specProvider = RopstenSpecProvider.Instance;
            ILogManager logger = NullLogManager.Instance;
            IDb codeDb = new MemDb();
            _stateDb = new SnapshotableDb(new MemDb());
            StateTree stateTree = new StateTree(_stateDb);
            _stateProvider = new StateProvider(stateTree, codeDb, logger);
            _storageDbProvider = new MemDbProvider(logger);
            Storage = new StorageProvider(_storageDbProvider, _stateProvider, logger);
            _ethereumSigner = new EthereumSigner(_specProvider, logger);
            IBlockhashProvider blockhashProvider = new TestBlockhashProvider();
            IVirtualMachine virtualMachine = new VirtualMachine(_stateProvider, Storage, blockhashProvider, logger);
            
            _processor = new TransactionProcessor(_specProvider, _stateProvider, Storage, virtualMachine, this, logger);
        }

        protected TransactionTrace TransactionTrace { get; private set; }

        public bool IsTracingEnabled { get; protected set; }
        public void SaveTrace(Keccak hash, TransactionTrace trace)
        {
            TransactionTrace = trace;
        }
        
        [Test]
        public void Stop()
        {
            TransactionReceipt receipt = Execute((byte)Instruction.STOP);
            Assert.AreEqual(GasCostOf.Transaction, receipt.GasUsed);
        }
        
        [SetUp]
        public void Setup()
        {
            IsTracingEnabled = false;
            TransactionTrace = null;

            _stateDbSnapshot = _stateDb.TakeSnapshot();
            _storageDbSnapshot = _storageDbProvider.TakeSnapshot();
            _stateRoot = _stateProvider.StateRoot;
        }

        private int _stateDbSnapshot;
        private int _storageDbSnapshot;
        private Keccak _stateRoot;

        [TearDown]
        public void TearDown()
        {
            Storage.ClearCaches();
            _stateProvider.Reset();
            _stateProvider.StateRoot = _stateRoot;

            _storageDbProvider.Restore(_storageDbSnapshot);
            _stateDb.Restore(_stateDbSnapshot);
        }
        
        protected TransactionReceipt Execute(params byte[] code)
        {
            _stateProvider.CreateAccount(A, 100.Ether());

            _stateProvider.CreateAccount(B, 100.Ether());
            Keccak codeHash = _stateProvider.UpdateCode(code);
            _stateProvider.UpdateCodeHash(TestObject.AddressB, codeHash, _specProvider.GenesisSpec);

            _stateProvider.Commit(_specProvider.GenesisSpec);

            Transaction transaction = Build.A.Transaction
                .WithGasLimit(100000)
                .WithGasPrice(1)
                .WithTo(TestObject.AddressB)
                .SignedAndResolved(_ethereumSigner, TestObject.PrivateKeyA, BlockNumber)
                .TestObject;

            Assert.AreEqual(A, _ethereumSigner.RecoverAddress(transaction, BlockNumber));

            Block block = Build.A.Block.WithNumber(BlockNumber).TestObject;
            TransactionReceipt receipt = _processor.Execute(transaction, block.Header);
            return receipt;
        }
        
        protected void AssertGas(TransactionReceipt receipt, long gas)
        {
            Assert.AreEqual(gas, receipt.GasUsed, "gas");
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