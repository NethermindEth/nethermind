// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Test;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.AccountAbstraction.Test
{
    [TestFixture]
    public class UserOperationTracerTests : VirtualMachineTestsBase
    {
        [TestCase(Instruction.GASPRICE, false)]
        [TestCase(Instruction.GASLIMIT, false)]
        [TestCase(Instruction.PREVRANDAO, false)]
        [TestCase(Instruction.TIMESTAMP, false)]
        [TestCase(Instruction.BASEFEE, false)]
        [TestCase(Instruction.BLOCKHASH, false)]
        [TestCase(Instruction.NUMBER, false)]
        [TestCase(Instruction.BALANCE, false)]
        [TestCase(Instruction.SELFBALANCE, false)]
        [TestCase(Instruction.ORIGIN, false)]
        [TestCase(Instruction.BALANCE, false)]
        [TestCase(Instruction.DUP1, true)]
        [TestCase(Instruction.ISZERO, true)]
        [TestCase(Instruction.AND, true)]
        public void Should_fail_if_banned_opcode_is_used_when_call_depth_is_more_than_one(Instruction instruction, bool success)
        {
            byte[] deployedCode = Prepare.EvmCode
                .PushData("0x01")
                .PushData("0x69")
                .Op(instruction)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, deployedCode, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;

            (UserOperationTxTracer tracer, _, _) = ExecuteAndTraceAccessCall(SenderRecipientAndMiner.Default, code);

            tracer.Success.Should().Be(success);
        }

        [TestCase(Instruction.NUMBER)]
        [TestCase(Instruction.GASPRICE)]
        public void Should_succeed_if_banned_opcode_is_used_with_calldepth_one(Instruction instruction)
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x69")
                .PushData("0x01")
                .Op(instruction)
                .Done;

            (UserOperationTxTracer tracer, _, _) = ExecuteAndTraceAccessCall(SenderRecipientAndMiner.Default, code);

            tracer.Success.Should().BeTrue();
        }

        [TestCase(false, false, Instruction.SSTORE, false)]
        [TestCase(false, false, Instruction.SLOAD, false)]
        [TestCase(false, true, Instruction.SSTORE, false)]
        [TestCase(false, true, Instruction.SLOAD, false)]
        [TestCase(true, false, Instruction.SSTORE, false)]
        [TestCase(true, false, Instruction.SLOAD, false)]
        [TestCase(true, true, Instruction.SSTORE, true)]
        [TestCase(true, true, Instruction.SLOAD, true)]
        public void Should_allow_external_storage_access_only_with_whitelisted_paymaster(bool paymasterValidation, bool whitelisted, Instruction opcodeToTest, bool shouldSucceed)
        {
            Address externalContractAddress = TestItem.GetRandomAddress();
            Address paymasterContractAddress = TestItem.GetRandomAddress();

            // simple storage access contract
            byte[] externalContractCalledByPaymasterCode = Prepare.EvmCode
                .PushData(69)
                .PushData(1)
                .Op(opcodeToTest)
                .Done;

            TestState.CreateAccount(externalContractAddress, 1.Ether());
            TestState.InsertCode(externalContractAddress, externalContractCalledByPaymasterCode, Spec);

            byte[] paymasterCode = Prepare.EvmCode
                .Call(externalContractAddress, 70000)
                .Done;

            TestState.CreateAccount(paymasterContractAddress, 1.Ether());
            TestState.InsertCode(paymasterContractAddress, paymasterCode, Spec);

            byte[] code = Prepare.EvmCode
                .Op(paymasterValidation ? Instruction.NUMBER : Instruction.BASEFEE) // switch to paymaster validation with NUMBER
                .Call(paymasterContractAddress, 100000)
                .Op(Instruction.STOP)
                .Done;

            (UserOperationTxTracer tracer, _, _) = ExecuteAndTraceAccessCall(SenderRecipientAndMiner.Default, code, whitelisted);

            tracer.Success.Should().Be(shouldSucceed);
        }

        [TestCase(false, false, true, false)]
        [TestCase(false, true, true, false)]
        [TestCase(true, false, true, false)]
        [TestCase(true, true, true, false)]
        [TestCase(false, false, false, true)]
        [TestCase(false, true, false, true)]
        [TestCase(true, false, false, true)]
        [TestCase(true, true, false, true)]
        public void Should_make_sure_external_contract_extcodehashes_stays_same_after_simulation(bool paymasterValidation, bool whitelisted, bool selfdestruct, bool shouldMatch)
        {
            Address externalContractAddress = TestItem.GetRandomAddress();
            Address paymasterContractAddress = TestItem.GetRandomAddress();

            // simple storage access contract
            byte[] externalContractCalledByPaymasterCode = Prepare.EvmCode
                .PushData(Address.Zero)
                .Op(selfdestruct ? Instruction.SELFDESTRUCT : Instruction.DUP1)
                .Done;

            TestState.CreateAccount(externalContractAddress, 1.Ether());
            TestState.InsertCode(externalContractAddress, externalContractCalledByPaymasterCode, Spec);

            byte[] paymasterCode = Prepare.EvmCode
                .Call(externalContractAddress, 70000)
                .Done;

            TestState.CreateAccount(paymasterContractAddress, 1.Ether());
            TestState.InsertCode(paymasterContractAddress, paymasterCode, Spec);

            byte[] code = Prepare.EvmCode
                .Op(paymasterValidation ? Instruction.NUMBER : Instruction.BASEFEE) // switch to paymaster validation with NUMBER
                .Call(paymasterContractAddress, 100000)
                .Op(Instruction.STOP)
                .Done;

            Keccak initialCodeHash = TestState.GetCodeHash(externalContractAddress);
            (UserOperationTxTracer tracer, _, _) = ExecuteAndTraceAccessCall(SenderRecipientAndMiner.Default, code, whitelisted);
            if (shouldMatch)
            {
                TestState.GetCodeHash(externalContractAddress).Should().Be(initialCodeHash);
            }
            else
            {
                TestState.GetCodeHash(externalContractAddress).Should().NotBe(initialCodeHash);
            }
        }

        [TestCase(200, true, true)]
        [TestCase(30000, false, false)]
        public void Should_not_allow_inner_call_to_run_out_of_gas(long gasLimit, bool shouldRunOutOfGas, bool shouldError)
        {
            Address externalContractAddress = TestItem.GetRandomAddress();
            Address paymasterContractAddress = TestItem.GetRandomAddress();

            // simple storage access contract
            byte[] externalContractCalledByPaymasterCode = Prepare.EvmCode
                .PushData(32)
                .PushData(0)
                .Op(Instruction.LOG0)
                .Done;

            TestState.CreateAccount(externalContractAddress, 1.Ether());
            TestState.InsertCode(externalContractAddress, externalContractCalledByPaymasterCode, Spec);

            byte[] paymasterCode = Prepare.EvmCode
                .Call(externalContractAddress, gasLimit)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;

            TestState.CreateAccount(paymasterContractAddress, 1.Ether());
            TestState.InsertCode(paymasterContractAddress, paymasterCode, Spec);

            byte[] code = Prepare.EvmCode
                .Call(paymasterContractAddress, 100000)
                .PushData(32)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.RETURNDATACOPY)
                .PushData(1)
                .PushData(31)
                .Op(Instruction.RETURN)
                .Done;

            (UserOperationTxTracer tracer, _, _) = ExecuteAndTraceAccessCall(SenderRecipientAndMiner.Default, code);
            tracer.Output.Should().BeEquivalentTo(shouldRunOutOfGas ? Bytes.FromHexString("0x00") : Bytes.FromHexString("0x01"));
            tracer.Error.Should().Be(shouldError ? "simulation failed: a call during simulation ran out of gas" : null);
        }

        [TestCase(Instruction.DELEGATECALL, true)]
        [TestCase(Instruction.CALL, true)]
        [TestCase(Instruction.STATICCALL, true)]
        [TestCase(Instruction.DUP1, false)]
        public void Should_allow_gas_only_if_followed_by_call(Instruction instruction, bool success)
        {
            byte[] deployedCode = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0) // value
                .PushData(TestItem.AddressF)
                .Op(Instruction.GAS)
                .Op(instruction)
                .Op(Instruction.STOP)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, deployedCode, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 70000)
                .Op(Instruction.STOP)
                .Done;

            (UserOperationTxTracer tracer, _, _) = ExecuteAndTraceAccessCall(SenderRecipientAndMiner.Default, code);

            tracer.Success.Should().Be(success);
        }

        private (UserOperationTxTracer trace, Block block, Transaction transaction) ExecuteAndTraceAccessCall(SenderRecipientAndMiner addresses, byte[] code, bool paymasterWhitelisted = false, bool firstSimulation = true)
        {
            (Block block, Transaction transaction) = PrepareTx(BlockNumber, 100000, code, addresses, 0);
            UserOperationTxTracer tracer = new(paymasterWhitelisted, firstSimulation, TestItem.AddressA, TestItem.AddressB, TestItem.AddressD, NullLogger.Instance);
            _processor.Execute(transaction, block.Header, tracer);
            return (tracer, block, transaction);
        }

        protected override long BlockNumber { get; } = MainnetSpecProvider.LondonBlockNumber;
    }
}
