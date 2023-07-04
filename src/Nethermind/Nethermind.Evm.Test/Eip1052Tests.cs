// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip1052Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => 10000001;

        protected override ISpecProvider SpecProvider => new CustomSpecProvider(
            ((ForkActivation)0, Byzantium.Instance), ((ForkActivation)10000001, Constantinople.Instance));

        [Test]
        public void Account_without_code_returns_empty_data_hash()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.EXTCODEHASH)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            AssertGas(result, 21000 + GasCostOf.VeryLow * 2 + GasCostOf.SSet + GasCostOf.ExtCodeHash);
            AssertStorage(UInt256.Zero, Keccak.OfAnEmptyString.Bytes);
        }

        [Test]
        public void Non_existing_account_returns_0()
        {
            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.EXTCODEHASH)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            AssertGas(result,
                21000 + GasCostOf.VeryLow * 2 + GasCostOf.SStoreNetMeteredEip1283 + GasCostOf.ExtCodeHash);
            AssertStorage(UInt256.Zero, Keccak.Zero);
        }

        [Test]
        public void Non_existing_precompile_returns_0()
        {
            Address precompileAddress = Sha256Precompile.Instance.Address;
            Assert.True(precompileAddress.IsPrecompile(Spec));

            byte[] code = Prepare.EvmCode
                .PushData(precompileAddress)
                .Op(Instruction.EXTCODEHASH)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            Execute(code);
            AssertStorage(UInt256.Zero, Keccak.Zero);
        }

        [Test]
        public void Existing_precompile_returns_empty_data_hash()
        {
            Address precompileAddress = Sha256Precompile.Instance.Address;
            Assert.True(precompileAddress.IsPrecompile(Spec));

            TestState.CreateAccount(precompileAddress, 1.Wei());

            byte[] code = Prepare.EvmCode
                .PushData(precompileAddress)
                .Op(Instruction.EXTCODEHASH)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            Execute(code);
            AssertStorage(UInt256.Zero, Keccak.OfAnEmptyString.Bytes);
        }

        [Test]
        public void Before_constantinople_throws_an_exception()
        {
            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.EXTCODEHASH)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput receipt = Execute(1000000, 100000, code);
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Failure));
        }

        [Test]
        public void Addresses_are_trimmed_properly()
        {
            byte[] addressWithGarbage = TestItem.AddressC.Bytes.PadLeft(32);
            addressWithGarbage[11] = 88;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, "some code"u8.ToArray(), Spec);

            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.EXTCODEHASH)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .PushData(addressWithGarbage)
                .Op(Instruction.EXTCODEHASH)
                .PushData(1)
                .Op(Instruction.SSTORE)
                .Done;

            Execute(code);
            Keccak codehash = Keccak.Compute("some code");
            AssertStorage(0, codehash.Bytes);
            AssertStorage(1, codehash.Bytes);
        }

        [Test]
        public void Self_destructed_returns_zero()
        {
            byte[] selfDestructCode = Prepare.EvmCode
                .PushData(Recipient)
                .Op(Instruction.SELFDESTRUCT).Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, selfDestructCode, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .PushData(TestItem.AddressC)
                .Op(Instruction.EXTCODEHASH)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            Execute(code);
            AssertStorage(0, Keccak.Compute(selfDestructCode));
        }

        [Test]
        public void Self_destructed_and_reverted_returns_code_hash()
        {
            byte[] callAndRevertCode = Prepare.EvmCode
                .Call(TestItem.AddressD, 50000)
                .Op(Instruction.REVERT).Done;

            byte[] selfDestructCode = Prepare.EvmCode
                .PushData(Recipient)
                .Op(Instruction.SELFDESTRUCT).Done;

            TestState.CreateAccount(TestItem.AddressD, 1.Ether());
            TestState.InsertCode(TestItem.AddressD, selfDestructCode, Spec);

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, callAndRevertCode, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .PushData(TestItem.AddressD)
                .Op(Instruction.EXTCODEHASH)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            Execute(code);
            AssertStorage(0, Keccak.Compute(selfDestructCode));
        }

        [Test]
        public void Empty_account_that_would_be_cleared_returns_zero()
        {
            TestState.CreateAccount(TestItem.AddressC, 0.Ether());

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 0)
                .PushData(TestItem.AddressC)
                .Op(Instruction.EXTCODEHASH)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            Execute(code);

            AssertStorage(UInt256.Zero, UInt256.Zero);
            Assert.False(TestState.AccountExists(TestItem.AddressC), "did not test the right thing - it was not an empty account + touch scenario");
        }

        [Test]
        public void Newly_created_empty_account_returns_empty_data_hash()
        {
            byte[] code = Prepare.EvmCode
                .Create(Array.Empty<byte>(), 0)
                .PushData(ContractAddress.From(Recipient, 0))
                .Op(Instruction.EXTCODEHASH)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            Execute(code);

            // todo: so far EIP does not define whether it should be zero or empty data
            AssertStorage(0, Keccak.OfAnEmptyString);
            Assert.True(TestState.AccountExists(ContractAddress.From(Recipient, 0)),
                "did not test the right thing - it was not a newly created empty account scenario");
        }

        [Test]
        public void Create_and_revert_returns_zero()
        {
            byte[] deployedCode = { 1, 2, 3 };

            byte[] initCode = Prepare.EvmCode
                .PushData(deployedCode.PadRight(32))
                .PushData(0)
                .Op(Instruction.MSTORE)
                .PushData(3)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            byte[] createCode = Prepare.EvmCode
                .Create(initCode, 0)
                .Op(Instruction.REVERT).Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, createCode, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .PushData(ContractAddress.From(TestItem.AddressC, 0))
                .Op(Instruction.EXTCODEHASH)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            Execute(code);
            AssertStorage(0, Keccak.Zero);
        }

        [Test]
        public void Create_returns_code_hash()
        {
            byte[] deployedCode = { 1, 2, 3 };
            Keccak deployedCodeHash = Keccak.Compute(deployedCode);

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode).Done;

            byte[] createCode = Prepare.EvmCode
                .Create(initCode, 0).Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, createCode, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .PushData(ContractAddress.From(TestItem.AddressC, 0))
                .Op(Instruction.EXTCODEHASH)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            Execute(code);
            AssertStorage(0, deployedCodeHash);
        }
    }
}
