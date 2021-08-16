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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.State;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip1014Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.ConstantinopleFixBlockNumber;

        private void AssertEip1014(Address address, byte[] code)
        {
            AssertCodeHash(address, Keccak.Compute(code));
        }

        [Test]
        public void TestHive()
        {
            byte[] code = Prepare.EvmCode
                .FromCode("0x73095e7baea6a6c7c4c2dfeb977efac326af552d873173095e7baea6a6c7c4c2dfeb977efac326af552d873173095e7baea6a6c7c4c2dfeb977efac326af552d87313700")
                .Done;
            
            Execute(code);
        }
        
        [Test]
        public void Test()
        {
            byte[] salt = {4, 5, 6};

            byte[] deployedCode = {1, 2, 3};

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode).Done;

            byte[] createCode = Prepare.EvmCode
                .Create2(initCode, salt, 0).Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .Done;

            Execute(code);

            Address expectedAddress = ContractAddress.From(TestItem.AddressC, salt.PadLeft(32).AsSpan(), initCode.AsSpan());
            AssertEip1014(expectedAddress, deployedCode);
        }

        [Test]
        public void Test_out_of_gas_existing_account()
        {
            byte[] salt = {4, 5, 6};
            byte[] deployedCode = {1, 2, 3};

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode).Done;

            byte[] createCode = Prepare.EvmCode
                .Create2(initCode, salt, 0).Done;

            Address expectedAddress = ContractAddress.From(TestItem.AddressC, salt.PadLeft(32).AsSpan(), initCode.AsSpan());

            TestState.CreateAccount(expectedAddress, 1.Ether());
            TestState.CreateAccount(TestItem.AddressC, 1.Ether());

            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 32100)
                .Done;

            Execute(code);

            TestState.GetAccount(expectedAddress).Should().NotBeNull();
            TestState.GetAccount(expectedAddress).Balance.Should().Be(1.Ether());
            AssertEip1014(expectedAddress, Array.Empty<byte>());
        }

        [Test]
        public void Test_out_of_gas_existing_account_with_storage()
        {
            byte[] salt = {4, 5, 6};
            byte[] deployedCode = {1, 2, 3};

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode).Done;

            byte[] createCode = Prepare.EvmCode
                .Create2(initCode, salt, 0).Done;

            Address expectedAddress = ContractAddress.From(TestItem.AddressC, salt.PadLeft(32).AsSpan(), initCode.AsSpan());

            TestState.CreateAccount(expectedAddress, 1.Ether());
            Storage.Set(new StorageCell(expectedAddress, 1), new byte[] {1, 2, 3, 4, 5});
            Storage.Commit();
            Storage.CommitTrees(0);
            TestState.Commit(Spec);
            TestState.CommitTree(0);
            
            Keccak storageRoot = TestState.GetAccount(expectedAddress).StorageRoot;
            storageRoot.Should().NotBe(PatriciaTree.EmptyTreeHash);

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());

            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 32100)
                .Done;

            Execute(code);

            TestState.GetAccount(expectedAddress).Should().NotBeNull();
            TestState.GetAccount(expectedAddress).Balance.Should().Be(1.Ether());
            TestState.GetAccount(expectedAddress).StorageRoot.Should().Be(storageRoot);
            AssertEip1014(expectedAddress, Array.Empty<byte>());
        }

        [Test]
        public void Test_out_of_gas_non_existing_account()
        {
            byte[] salt = {4, 5, 6};
            byte[] deployedCode = {1, 2, 3};

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode).Done;

            byte[] createCode = Prepare.EvmCode
                .Create2(initCode, salt, 0).Done;

            Address expectedAddress = ContractAddress.From(TestItem.AddressC, salt.PadLeft(32).AsSpan(), initCode.AsSpan());

            // TestState.CreateAccount(expectedAddress, 1.Ether()); <-- non-existing
            TestState.CreateAccount(TestItem.AddressC, 1.Ether());

            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 32100)
                .Done;

            Execute(code);

            TestState.AccountExists(expectedAddress).Should().BeFalse();
        }

        /// <summary>
        /// https://eips.ethereum.org/EIPS/eip-1014
        /// </summary>
        [TestCase("0x0000000000000000000000000000000000000000", "0x0000000000000000000000000000000000000000000000000000000000000000", "0x00", 32006, "0x4D1A2e2bB4F88F0250f26Ffff098B0b30B26BF38")]
        [TestCase("0xdeadbeef00000000000000000000000000000000", "0x0000000000000000000000000000000000000000000000000000000000000000", "0x00", 32006, "0xB928f69Bb1D91Cd65274e3c79d8986362984fDA3")]
        [TestCase("0xdeadbeef00000000000000000000000000000000", "0x000000000000000000000000feed000000000000000000000000000000000000", "0x00", 32006, "0xD04116cDd17beBE565EB2422F2497E06cC1C9833")]
        [TestCase("0x0000000000000000000000000000000000000000", "0x0000000000000000000000000000000000000000000000000000000000000000", "0xdeadbeef", 32006, "0x70f2b2914A2a4b783FaEFb75f459A580616Fcb5e")]
        [TestCase("0x00000000000000000000000000000000deadbeef", "0x00000000000000000000000000000000000000000000000000000000cafebabe", "0xdeadbeef", 32006, "0x60f3f640a8508fC6a86d45DF051962668E1e8AC7")]
        [TestCase("0x00000000000000000000000000000000deadbeef", "0x00000000000000000000000000000000000000000000000000000000cafebabe", "0xdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef", 32012, "0x1d8bfDC5D46DC4f61D6b6115972536eBE6A8854C")]
        [TestCase("0x0000000000000000000000000000000000000000", "0x0000000000000000000000000000000000000000000000000000000000000000", "0x", 32000, "0xE33C0C7F7df4809055C3ebA6c09CFe4BaF1BD9e0")]
        public void Examples_from_eip_spec_are_executed_correctly(string addressHex, string saltHex, string initCodeHex, long gas, string resultHex)
        {
            byte[] salt = Bytes.FromHexString(saltHex);

            byte[] deployedCode = Array.Empty<byte>();

            byte[] initCode = Bytes.FromHexString(initCodeHex);

            byte[] createCode = Prepare.EvmCode
                .Create2(initCode, salt, 0).Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .Done;

            var trace = ExecuteAndTrace(code);

            Address expectedAddress = new(resultHex);
            AssertEip1014(expectedAddress, deployedCode);
//            Assert.AreEqual(gas, trace.Entries.Single(e => e.Operation == Instruction.CREATE2.ToString()).GasCost);
        }
    }
}
