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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class VirtualMachineTests
    {
        public VirtualMachineTests()
        {
            _spec = RopstenSpecProvider.Instance;
            ILogger logger = NullLogger.Instance;
            IDb codeDb = new InMemoryDb();
            _stateDb = new InMemoryDb();
            StateTree stateTree = new StateTree(_stateDb);
            _stateProvider = new StateProvider(stateTree, logger, codeDb);
            _storageDbProvider = new DbProvider(logger);
            _storageProvider = new StorageProvider(_storageDbProvider, _stateProvider, logger);
            _ethereumSigner = new EthereumSigner(_spec, logger);
            IBlockhashProvider blockhashProvider = new TestBlockhashProvider();
            IVirtualMachine virtualMachine = new VirtualMachine(_spec, _stateProvider, _storageProvider, blockhashProvider, logger);
            _processor = new TransactionProcessor(_spec, _stateProvider, _storageProvider, virtualMachine, _ethereumSigner, logger);
            
            _stateDbSnapshot = _stateDb.TakeSnapshot();
            _storageDbSnapshot = _storageDbProvider.TakeSnapshot();
            _stateRoot = _stateProvider.StateRoot;
        }

        private readonly int _stateDbSnapshot;
        private readonly int _storageDbSnapshot;
        private readonly Keccak _stateRoot;

        [TearDown]
        public void TearDown()
        {
            _storageProvider.ClearCaches();
            _stateProvider.ClearCaches();
            _stateProvider.StateRoot = _stateRoot;

            _storageDbProvider.Restore(_storageDbSnapshot);
            _stateDb.Restore(_stateDbSnapshot);
        }

        private readonly IEthereumSigner _ethereumSigner;
        private readonly ITransactionProcessor _processor;
        private readonly ISpecProvider _spec;
        private readonly IDb _stateDb;
        private readonly IDbProvider _storageDbProvider;
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;

        private TransactionReceipt Execute(params byte[] code)
        {
            _stateProvider.CreateAccount(A, 100.Ether());

            _stateProvider.CreateAccount(B, 100.Ether());
            Keccak codeHash = _stateProvider.UpdateCode(code);
            _stateProvider.UpdateCodeHash(TestObject.AddressB, codeHash, _spec.GenesisSpec);

            _stateProvider.Commit(_spec.GenesisSpec);

            Transaction transaction = Build.A.Transaction
                .WithGasLimit(100000)
                .WithGasPrice(1)
                .WithTo(TestObject.AddressB)
                .Signed(_ethereumSigner, TestObject.PrivateKeyA, 100000)
                .TestObject;

            Assert.AreEqual(A, _ethereumSigner.RecoverAddress(transaction, 100000));

            Block block = Build.A.Block.WithNumber(10000).TestObject;
            TransactionReceipt receipt = _processor.Execute(transaction, block.Header);
            return receipt;
        }

        [Test]
        public void Stop()
        {
            TransactionReceipt receipt = Execute(
                (byte)Instruction.STOP);
            Assert.AreEqual(GasCostOf.Transaction, receipt.GasUsed);
        }

        private static readonly Address A = TestObject.AddressA;
        private static readonly Address B = TestObject.AddressB;
        
        [Test]
        public void Add_0_0()
        {
            TransactionReceipt receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.ADD,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.AreEqual(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SReset, receipt.GasUsed, "gas");
            Assert.AreEqual(new byte[] {0}, _storageProvider.Get(B, 0), "storage");
        }
        
        [Test]
        public void Add_0_1()
        {
            TransactionReceipt receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                1,
                (byte)Instruction.ADD,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.AreEqual(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet, receipt.GasUsed, "gas");
            Assert.AreEqual(new byte[] {1}, _storageProvider.Get(B, 0), "storage");
        }
        
        [Test]
        public void Add_1_0()
        {
            TransactionReceipt receipt = Execute(
                (byte)Instruction.PUSH1,
                1,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.ADD,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.AreEqual(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet, receipt.GasUsed, "gas");
            Assert.AreEqual(new byte[] {1}, _storageProvider.Get(B, 0), "storage");
        }
    }
}