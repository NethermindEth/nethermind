// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip8037RegressionTests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    /// <summary>
    /// When a nested CREATE's child frame has too little regular gas to cover both
    /// the regular code deposit cost AND the state-gas spill, the CREATE must fail.
    ///
    /// Gas budget (1-byte deployed contract, child state reservoir = 0):
    ///   regularDepositCost  = 6  (CodeDepositRegularPerWord x 1 word)
    ///   stateDepositCost    = 1174 (CostPerStateByte x 1 byte)
    ///   stateSpill          = 1174 (entire stateDepositCost spills into regular gas)
    ///   total regular needed = 6 + 1174 = 1180
    ///
    /// Child ends with 1175 regular gas after init code.
    /// Without the fix, the pre-check passes (1175 >= 6 and 1175 >= 1174) and the
    /// charge runs on the merged parent+child pool, silently borrowing parent gas.
    /// </summary>
    [Test]
    public void Eip8037_nested_create_code_deposit_must_not_borrow_parent_regular_gas()
    {
        // Init code: deploys 1 byte of zeros from memory
        // PUSH1 1, PUSH1 0, RETURN = 5 bytes, costs 9 gas (3+3+3 memory expansion)
        byte[] initCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        // Factory code: CREATE(value=0, initCode), then RETURN the result (address or 0)
        byte[] factoryCode = Prepare.EvmCode
            .Create(initCode, UInt256.Zero)
            // Stack: [address or 0]
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        // Gas calculation:
        //   Intrinsic (CALL to existing account): 21000
        //   Factory pre-CREATE opcodes: 21 gas
        //   CREATE opcode costs:
        //     CreateRegular(9000) + InitCodeWord(2) = 9002 regular
        //     CreateState(131488) -> spills entirely to regular (factory has 0 state reservoir)
        //     Total: 140490 regular
        //   Remaining after CREATE costs: 1202
        //   63/64 rule: callGas = 1202 - floor(1202/64) = 1184, factory retains 18
        //   Child: 1184 gas -> 9 for init code -> 1175 remaining for code deposit
        //   Factory post-CREATE: 12 gas (PUSH, MSTORE, PUSH, PUSH, RETURN)
        //   Total: 21000 + 21 + 140490 + 1202 = 162713
        long gasLimit = 162713;

        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, factoryCode);

        // Transaction succeeds (factory runs fine), but the nested CREATE must fail
        // because the child can't afford the code deposit from its own gas alone.
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), "Factory execution should succeed");

        // CREATE result: 0 = failure (returned in the 32-byte output)
        byte[] returnData = tracer.ReturnValue;
        Assert.That(returnData.IsZero(), Is.True,
            "Nested CREATE should fail: child has 1175 gas but needs 1180 for code deposit (6 regular + 1174 state spill)");
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.CreateState));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.LessThan(tracer.GasConsumedResult.BlockStateGas));
    }

    [Test]
    public void Eip8037_nested_create_code_deposit_failure_must_restore_create_state_to_parent()
    {
        byte[] childInitCode = Prepare.EvmCode
            .PushData(33_000)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        byte[] code = Prepare.EvmCode
            .Create(childInitCode, UInt256.Zero)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 5_000_000, code);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
    }

    /// <summary>
    /// When a top-level CREATE tx's initcode performs a CALL to a new account and the
    /// later code deposit fails, all state gas should be discarded from block-state
    /// accounting.
    ///
    /// The exceptional halt restores the CREATE state reservoir together with any
    /// initcode state gas, so block_state must return to zero.
    /// </summary>
    [Test]
    public void Eip8037_code_deposit_failure_must_discard_initcode_state_gas_from_block_accumulator()
    {
        // Initcode:
        //   1) CALL a dead account with value=1 - charges NewAccountState (131,488)
        //   2) RETURN 33,000 bytes - exceeds MaxCodeSizeEip7954 (32,768), triggers deposit failure
        byte[] initCode = Prepare.EvmCode
            .CallWithValue(TestItem.AddressC, 20_000, UInt256.One)
            .PushData(33_000)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        long gasLimit = 1_000_000;

        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, initCode, value: 1, blockGasLimit: 50_000_000);
        transaction.To = null;
        transaction.Data = initCode;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure),
            "CREATE tx with oversized code return should fail");
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas,
            Is.EqualTo(gasLimit - GasCostOf.CreateState - GasCostOf.NewAccountState));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        Assert.That(TestState.AccountExists(TestItem.AddressC), Is.False,
            "The initcode CALL target must be reverted together with its state gas.");
    }

    [Test]
    public void Eip8037_oversized_initcode_create_must_keep_create_state_out_of_block_state_gas()
    {
        byte[] createFromCalldataCode = Prepare.EvmCode
            .CALLDATASIZE()
            .CALLDATACOPY(0, 0)
            .CALLDATASIZE()
            .CREATEx(1, 0, 0, null)
            .Op(Instruction.STOP)
            .Done;

        byte[] oversizedInitCode = new byte[(int)Spec.MaxInitCodeSize + 1];
        const long gasLimit = 1_000_000;

        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, createFromCalldataCode, oversizedInitCode, UInt256.Zero);

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(gasLimit));
    }

    [TestCase(false, TestName = "Eip8037_create_in_static_context_must_not_charge_state_gas_or_increment_nonce_CREATE")]
    [TestCase(true, TestName = "Eip8037_create_in_static_context_must_not_charge_state_gas_or_increment_nonce_CREATE2")]
    public void Eip8037_create_in_static_context_must_not_charge_state_gas_or_increment_nonce(bool create2)
    {
        byte[] childInitCode = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;

        byte[] createAttemptCode = create2
            ? Prepare.EvmCode.Create2(childInitCode, [0x01], UInt256.Zero).Op(Instruction.STOP).Done
            : Prepare.EvmCode.Create(childInitCode, UInt256.Zero).Op(Instruction.STOP).Done;
        Address createdAddress = create2
            ? ContractAddress.From(TestItem.AddressC, [0x01], childInitCode)
            : ContractAddress.From(TestItem.AddressC, 0);

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, createAttemptCode, SpecProvider.GenesisSpec);

        byte[] outerCode = Prepare.EvmCode
            .StaticCall(TestItem.AddressC, 50_000)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 200_000, outerCode);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        Assert.That(TestState.GetNonce(TestItem.AddressC), Is.EqualTo(UInt256.Zero));
        Assert.That(TestState.AccountExists(createdAddress), Is.False);
    }

    [TestCase(false, TestName = "Eip8037_static_create_followed_by_parent_sstore_must_not_leak_create_state_gas_CREATE")]
    [TestCase(true, TestName = "Eip8037_static_create_followed_by_parent_sstore_must_not_leak_create_state_gas_CREATE2")]
    public void Eip8037_static_create_followed_by_parent_sstore_must_not_leak_create_state_gas(bool create2)
    {
        byte[] childInitCode = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;

        byte[] createAttemptCode = create2
            ? Prepare.EvmCode.Create2(childInitCode, [0x01], UInt256.Zero).Op(Instruction.STOP).Done
            : Prepare.EvmCode.Create(childInitCode, UInt256.Zero).Op(Instruction.STOP).Done;
        Address createdAddress = create2
            ? ContractAddress.From(TestItem.AddressC, [0x01], childInitCode)
            : ContractAddress.From(TestItem.AddressC, 0);

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, createAttemptCode, SpecProvider.GenesisSpec);
        TestState.Set(new StorageCell(Recipient, 0), [0xDE, 0xAD]);

        byte[] outerCode = Prepare.EvmCode
            .StaticCall(TestItem.AddressC, 200_000)
            .PushData(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .PushData(1)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 500_000, outerCode);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(259_698));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(226_930));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.SSetState));
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 0 }));
        Assert.That(TestState.Get(new StorageCell(Recipient, 1)).ToArray(), Is.EqualTo(new byte[] { 1 }));
        Assert.That(TestState.GetNonce(TestItem.AddressC), Is.EqualTo(UInt256.Zero));
        Assert.That(TestState.AccountExists(createdAddress), Is.False);
    }

    [Test]
    public void Eip8037_block_validation_must_not_use_header_max_gas_used_as_remaining_tx_budget()
    {
        byte[] stateHeavyCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        const long blockGasLimit = 500_000;
        (Block block, Transaction firstTx) = PrepareTx(Activation, 100_000, stateHeavyCode, value: 0, blockGasLimit: blockGasLimit);

        TestAllTracerWithOutput firstTracer = CreateTracer();
        TransactionResult firstResult = _processor.Execute(
            firstTx,
            new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
            firstTracer);

        Assert.That(firstResult, Is.EqualTo(TransactionResult.Ok));
        Assert.That(firstTracer.GasConsumedResult.BlockStateGas, Is.GreaterThan(firstTx.BlockGasUsed),
            "The first tx should be state-gas dominated so header.gasUsed tracks the state dimension.");

        long legacyRemaining = block.Header.GasLimit - block.Header.GasUsed;
        long secondTxGasLimit = legacyRemaining + 1;

        SenderRecipientAndMiner secondParticipants = new()
        {
            SenderKey = TestItem.PrivateKeyC,
            RecipientKey = TestItem.PrivateKeyD,
            MinerKey = TestItem.PrivateKeyD,
        };
        (_, Transaction secondTx) = PrepareTx(
            Activation,
            secondTxGasLimit,
            null,
            senderRecipientAndMiner: secondParticipants,
            value: 0,
            blockGasLimit: blockGasLimit);

        TestAllTracerWithOutput secondTracer = CreateTracer();
        TransactionResult secondResult = _processor.Execute(
            secondTx,
            new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
            secondTracer);

        Assert.That(secondTxGasLimit, Is.GreaterThan(legacyRemaining));
        Assert.That(secondResult, Is.EqualTo(TransactionResult.Ok));
        Assert.That(secondTracer.GasConsumedResult.EffectiveBlockGas, Is.LessThan(legacyRemaining));
        Assert.That(block.Header.GasUsed, Is.LessThanOrEqualTo(block.Header.GasLimit));
    }

    /// <summary>
    /// A child CALL that runs out of gas during SSTORE must not spill state gas into the
    /// parent frame's reservoir. If it does, the parent can incorrectly complete its own
    /// SSTORE with gas that should have been burned by the child halt.
    /// </summary>
    [Test]
    public void Eip8037_failed_child_sstore_must_not_inflate_parent_state_reservoir()
    {
        byte[] childCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, childCode, SpecProvider.GenesisSpec);

        byte[] parentCode = Prepare.EvmCode
            .Call(TestItem.AddressC, 40_000)
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 70_000, parentCode);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure),
            "The parent SSTORE should run out of gas once the child CALL burns its own failed SSTORE gas.");
        Assert.That(tracer.Error, Is.EqualTo("OutOfGas"));
    }
}
