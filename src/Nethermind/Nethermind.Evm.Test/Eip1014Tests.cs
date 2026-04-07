// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Evm.State;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip1014Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.ConstantinopleFixBlockNumber;

        private static readonly byte[] _defaultSalt = [4, 5, 6];
        private static readonly byte[] _defaultDeployedCode = [1, 2, 3];
        private static readonly byte[] _defaultInitCode = Prepare.EvmCode.ForInitOf(_defaultDeployedCode).Done;

        private void AssertEip1014(Address address, byte[] code) => AssertCodeHash(address, Keccak.Compute(code));

        /// <summary>
        /// Sets up a CREATE2 deployer contract at <see cref="VirtualMachineTestsBase.TestItem.AddressC"/>
        /// and returns the deterministic deployment address together with the outer call bytecode.
        /// </summary>
        private (Address expectedAddress, byte[] callCode) PrepareCreate2(byte[] salt, byte[] initCode, long callGas = 50000)
        {
            byte[] createCode = Prepare.EvmCode.Create2(initCode, salt, 0).Done;
            Address expectedAddress = ContractAddress.From(TestItem.AddressC, salt.PadLeft(32).AsSpan(), initCode.AsSpan());

            TestState.CreateAccount(TestItem.AddressC, 1.Ether);
            TestState.InsertCode(TestItem.AddressC, createCode, Spec);

            byte[] callCode = Prepare.EvmCode.Call(TestItem.AddressC, callGas).Done;
            return (expectedAddress, callCode);
        }

        [Test]
        public void TestHive() =>
            Execute(Prepare.EvmCode
                .FromCode("0x73095e7baea6a6c7c4c2dfeb977efac326af552d873173095e7baea6a6c7c4c2dfeb977efac326af552d873173095e7baea6a6c7c4c2dfeb977efac326af552d87313700")
                .Done);

        [Test]
        public void Test()
        {
            (Address expectedAddress, byte[] callCode) = PrepareCreate2(_defaultSalt, _defaultInitCode);
            Execute(callCode);
            AssertEip1014(expectedAddress, _defaultDeployedCode);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Test_out_of_gas_existing_account(bool withStorage)
        {
            (Address expectedAddress, byte[] callCode) = PrepareCreate2(_defaultSalt, _defaultInitCode, callGas: 32100);

            TestState.CreateAccount(expectedAddress, 1.Ether);

            if (withStorage)
            {
                TestState.Set(new StorageCell(expectedAddress, 1), [1, 2, 3, 4, 5]);
                TestState.Commit(Spec);
                TestState.CommitTree(0);
                TestState.IsStorageEmpty(expectedAddress).Should().BeFalse();
            }

            Execute(callCode);

            TestState.TryGetAccount(expectedAddress, out AccountStruct account).Should().BeTrue();
            account.Balance.Should().Be(1.Ether);
            AssertEip1014(expectedAddress, []);
        }

        [Test]
        public void Test_out_of_gas_non_existing_account()
        {
            (Address expectedAddress, byte[] callCode) = PrepareCreate2(_defaultSalt, _defaultInitCode, callGas: 32100);
            Execute(callCode);
            TestState.AccountExists(expectedAddress).Should().BeFalse();
        }

        [Test]
        public void Test_collision_with_7702_delegated_account_is_rejected()
        {
            byte[] salt = [0];
            byte[] initCode = [0x00];
            (Address expectedAddress, byte[] callCode) = PrepareCreate2(salt, initCode);

            Address delegateTarget = new("0x0000000000000000000000000000000000000042");
            TestState.CreateAccount(expectedAddress, UInt256.Zero);
            CodeInfoRepository.SetDelegation(delegateTarget, expectedAddress, SpecProvider.GetSpec(MainnetSpecProvider.OsakaActivation));
            TestState.Commit(SpecProvider.GenesisSpec);
            TestState.CommitTree(0);
            Execute(MainnetSpecProvider.OsakaActivation, callCode);
            TestState.GetCode(expectedAddress).Should().NotBeEmpty("delegation code should be preserved");
            Eip7702Constants.IsDelegatedCode(TestState.GetCode(expectedAddress)).Should().BeTrue("original delegation should remain");
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
            (_, byte[] callCode) = PrepareCreate2(Bytes.FromHexString(saltHex), Bytes.FromHexString(initCodeHex));
            ExecuteAndTrace(callCode);
            AssertEip1014(new(resultHex), []);
        }
    }
}
