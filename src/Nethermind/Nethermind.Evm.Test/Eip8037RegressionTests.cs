// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip8037RegressionTests : VirtualMachineTestsBase
{
    private const long DynamicStatePricingBlockGasLimit = 100_000_000;

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

        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, factoryCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        // Transaction succeeds (factory runs fine), but the nested CREATE must fail
        // because the child can't afford the code deposit from its own gas alone.
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), "Factory execution should succeed");

        // CREATE result: 0 = failure (returned in the 32-byte output)
        byte[] returnData = tracer.ReturnValue;
        Assert.That(returnData.IsZero(), Is.True,
            "Nested CREATE should fail: child has 1175 gas but needs 1180 for code deposit (6 regular + 1174 state spill)");
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.GreaterThan(0));
    }

    [Test]
    public void Eip8037_nested_create_code_deposit_failure_must_refund_parent_create_state()
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

        TestAllTracerWithOutput tracer = Execute(Activation, 5_000_000, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
    }

    /// <summary>
    /// When a top-level CREATE tx's initcode performs a CALL to a new account and the
    /// later code deposit fails, the reverted initcode state gas must unwind back to
    /// the intrinsic CREATE floor before final gas accounting.
    ///
    /// Only the top-level CREATE state gas should remain in block_state_gas_used; the
    /// reverted initcode state growth must not contribute to the block header gas_used.
    /// </summary>
    [Test]
    public void Eip8037_code_deposit_failure_must_keep_initcode_state_gas_in_state_dimension()
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

        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, initCode, value: 1, blockGasLimit: DynamicStatePricingBlockGasLimit);
        transaction.To = null;
        transaction.Data = initCode;

        TransactionProcessor<EthereumGasPolicy> parallelProcessor = new(
            BlobBaseFeeCalculator.Instance,
            SpecProvider,
            TestState,
            Machine,
            CodeInfoRepository,
            GetLogManager(),
            parallel: true);
        parallelProcessor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)));

        TestAllTracerWithOutput tracer = CreateTracer();
        parallelProcessor.Execute(transaction, tracer);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure),
            "CREATE tx with oversized code return should fail");
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.LessThan(gasLimit),
            "Unused gas should still be refunded on top-level code deposit failure.");
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas,
            Is.EqualTo(tracer.GasConsumedResult.SpentGas - GasCostOf.CreateState));
        Assert.That(tracer.GasConsumedResult.BlockStateGas,
            Is.EqualTo(GasCostOf.CreateState));
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
        long gasLimit = Eip7825Constants.DefaultTxGasLimitCap + GasCostOf.CreateState;

        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, createFromCalldataCode, oversizedInitCode, UInt256.Zero);
        block.Header.GasLimit = DynamicStatePricingBlockGasLimit;

        TestAllTracerWithOutput tracer = CreateTracer();
        TransactionResult result = _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        Assert.That(result, Is.EqualTo(TransactionResult.Ok));
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(Eip7825Constants.DefaultTxGasLimitCap));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(Eip7825Constants.DefaultTxGasLimitCap));
    }

    [Test]
    public void Eip8037_top_level_create_collision_must_still_pay_calldata_floor()
    {
        byte[] initCode = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;

        const long gasLimit = 600_000;
        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, initCode, value: 0, blockGasLimit: DynamicStatePricingBlockGasLimit);
        transaction.To = null;
        transaction.Data = initCode;

        Address contractAddress = ContractAddress.From(transaction.SenderAddress!, transaction.Nonce);
        TestState.CreateAccount(contractAddress, 0, 1);

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        long expectedRegularGas = GasCostOf.Transaction + GasCostOf.CreateRegular + GasCostOf.TxDataZero + GasCostOf.InitCodeWord;
        long expectedFloorGas = GasCostOf.Transaction + 4 * GasCostOf.TotalCostFloorPerTokenEip7976;

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.CreateState));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(Math.Max(expectedRegularGas, expectedFloorGas)));
    }

    [TestCase(false, TestName = "Eip8037_top_level_create_revert_must_refund_inner_create_state_gas")]
    [TestCase(true, TestName = "Eip8037_top_level_create_halt_must_refund_inner_create_state_gas")]
    public void Eip8037_top_level_create_failure_must_refund_inner_create_state_gas(bool exceptionalHalt)
    {
        byte[] childInitCode = Prepare.EvmCode
            .ForInitOf(Prepare.EvmCode.Op(Instruction.STOP).Done)
            .Done;

        Prepare initCodeBuilder = Prepare.EvmCode
            .Create(childInitCode, UInt256.Zero)
            .Op(Instruction.POP);

        byte[] initCode = exceptionalHalt
            ? initCodeBuilder.Op(Instruction.INVALID).Done
            : initCodeBuilder
                .PushData(0)
                .PushData(0)
                .Op(Instruction.REVERT)
                .Done;

        (Block block, Transaction transaction) = PrepareTx(Activation, 600_000, initCode, value: 0, blockGasLimit: DynamicStatePricingBlockGasLimit);
        transaction.To = null;
        transaction.Data = initCode;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.CreateState),
            "Only the intrinsic top-level create state gas should remain after the initcode failure reverts the inner CREATE.");
    }

    [Test]
    public void Eip8037_create_memory_oog_must_not_charge_create_state_gas()
    {
        byte[] code = Prepare.EvmCode
            .PushData(16_777_216)
            .PushData(0)
            .PushData(0)
            .Op(Instruction.CREATE)
            .Done;

        const long gasLimit = 100_000;
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(gasLimit));
    }

    [TestCase(false, TestName = "Eip8037_create_in_static_context_must_not_charge_state_gas_or_increment_nonce_CREATE")]
    [TestCase(true, TestName = "Eip8037_create_in_static_context_must_not_charge_state_gas_or_increment_nonce_CREATE2")]
    public void Eip8037_create_in_static_context_must_not_charge_state_gas_or_increment_nonce(bool create2)
    {
        Address createdAddress = SetupStaticCreateAttempt(create2);

        byte[] outerCode = Prepare.EvmCode
            .StaticCall(TestItem.AddressC, 50_000)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 200_000, outerCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        Assert.That(TestState.GetNonce(TestItem.AddressC), Is.EqualTo(UInt256.Zero));
        Assert.That(TestState.AccountExists(createdAddress), Is.False);
    }

    [TestCase(false, TestName = "Eip8037_static_create_followed_by_parent_sstore_must_not_leak_create_state_gas_CREATE")]
    [TestCase(true, TestName = "Eip8037_static_create_followed_by_parent_sstore_must_not_leak_create_state_gas_CREATE2")]
    public void Eip8037_static_create_followed_by_parent_sstore_must_not_leak_create_state_gas(bool create2)
    {
        Address createdAddress = SetupStaticCreateAttempt(create2);
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

        TestAllTracerWithOutput tracer = Execute(Activation, 500_000, outerCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(259_698));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(226_930));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.SSetState));
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 0 }));
        Assert.That(TestState.Get(new StorageCell(Recipient, 1)).ToArray(), Is.EqualTo(new byte[] { 1 }));
        Assert.That(TestState.GetNonce(TestItem.AddressC), Is.EqualTo(UInt256.Zero));
        Assert.That(TestState.AccountExists(createdAddress), Is.False);
    }

    private Address SetupStaticCreateAttempt(bool create2)
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

        return createdAddress;
    }

    /// <summary>
    /// Top-level halt is billed at the original regular-gas budget (here = gasLimit, since
    /// gasLimit &lt; cap leaves no reservoir excess). State gas that was borrowed from
    /// execution gas (spilled) stays burned with the regular gas — no user refund, and it
    /// does not contribute to BlockStateGas because the regular burn already accounts for
    /// it via BlockGas.
    /// </summary>
    [Test]
    public void Eip8037_top_level_exceptional_halt_burns_spilled_state_gas()
    {
        Prepare codeBuilder = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE);

        for (int i = 0; i < 1_025; i++)
        {
            codeBuilder.Op(Instruction.PUSH0);
        }

        const long gasLimit = 100_000;
        byte[] code = codeBuilder.Done;

        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.Error, Is.EqualTo(nameof(EvmExceptionType.StackOverflow)));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
        Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(gasLimit));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(gasLimit));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        AssertStorage(new StorageCell(Recipient, 0), UInt256.Zero);
    }

    /// <summary>
    /// Top-level REVERT preserves gas_left, so the spilled portion of state_gas_used —
    /// originally drawn from gas_left — is still in the user's pocket. The full
    /// state_gas_used (reservoir-portion AND spilled-portion) must be refunded to the
    /// reservoir; the user is billed only the regular component (which already paid for
    /// the spill in gas_left). BlockStateGas ends at 0; SpentGas is below the limit
    /// because gas_left survives the revert.
    /// </summary>
    [Test]
    public void Eip8037_top_level_revert_refunds_full_spilled_state_gas()
    {
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .PushData(0)
            .PushData(0)
            .Op(Instruction.REVERT)
            .Done;

        const long gasLimit = 100_000;
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.LessThan(gasLimit));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        AssertStorage(new StorageCell(Recipient, 0), UInt256.Zero);
    }

    /// <summary>
    /// Same rule as the SSTORE-spill case but for an inner CALL whose NewAccountState charge
    /// spills into regular gas before the parent INVALIDs at the top level.
    /// </summary>
    [Test]
    public void Eip8037_top_level_exceptional_halt_burns_spilled_child_state_gas()
    {
        byte[] code = Prepare.EvmCode
            .CallWithValue(TestItem.AddressC, 50_000, UInt256.One)
            .Op(Instruction.POP)
            .Op(Instruction.INVALID)
            .Done;

        const long gasLimit = 600_000;
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(gasLimit));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        Assert.That(TestState.AccountExists(TestItem.AddressC), Is.False);
    }

    [Test]
    public void Eip8037_exceptional_halt_must_restore_child_inline_state_refund()
    {
        byte[] childCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .PushData(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.INVALID)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, childCode, SpecProvider.GenesisSpec);

        byte[] code = Prepare.EvmCode
            .Call(TestItem.AddressC, 100_000)
            .Op(Instruction.POP)
            .Op(Instruction.INVALID)
            .Done;

        long gasLimit = Eip7825Constants.DefaultTxGasLimitCap + GasCostOf.SSetState;
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(Eip7825Constants.DefaultTxGasLimitCap));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(Eip7825Constants.DefaultTxGasLimitCap));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        AssertStorage(new StorageCell(TestItem.AddressC, UInt256.Zero), UInt256.Zero);
    }

    /// <summary>
    /// Mirrors EEST <c>test_reservoir_restored_after_child_spill_and_halt</c> (failing on CI).
    /// Outer contract calls child with 500k gas. Child does 2 SSTOREs, then INVALID-halts.
    /// Outer continues and does 2 more SSTOREs, then STOP. Goal: probe the halt-restore
    /// path where parent's reservoir must be returned to a state where its 2 outer SSTOREs
    /// can succeed.
    /// </summary>
    [Test]
    public void Eip8037_reservoir_restored_after_child_spill_and_halt()
    {
        // Child: SSTORE 1 -> slot 0, SSTORE 1 -> slot 1, INVALID
        byte[] childCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .PushData(1)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .Op(Instruction.INVALID)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 0);
        TestState.InsertCode(TestItem.AddressC, childCode, SpecProvider.GenesisSpec);

        // Outer: CALL(child, 500k gas, value=0), POP, SSTORE 1 -> slot 0, SSTORE 1 -> slot 1, STOP
        byte[] outerCode = Prepare.EvmCode
            .Call(TestItem.AddressC, 500_000)
            .Op(Instruction.POP)
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .PushData(1)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        // Use the EEST fixture's tx gas limit so the math lines up.
        const long gasLimit = 0x010092c0; // 16_815_296
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, outerCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        // Parent's two outer SSTOREs must have landed.
        AssertStorage(new StorageCell(Recipient, 0), UInt256.One);
        AssertStorage(new StorageCell(Recipient, 1), UInt256.One);
        // Child's SSTOREs reverted on halt.
        AssertStorage(new StorageCell(TestItem.AddressC, 0), UInt256.Zero);
        AssertStorage(new StorageCell(TestItem.AddressC, 1), UInt256.Zero);
        // The child's spilled state-gas stays burned with the child's regular gas — must NOT
        // propagate to the parent's StateGasSpill bucket, otherwise Calculate8037BlockRegularGas
        // would subtract the already-burned amount from the block's regular gas total and
        // undercount block.gasUsed by 1×SSetState. Pre-fix this asserted 489_367.
        Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(526_935));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(2 * GasCostOf.SSetState));
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

        const long blockGasLimit = DynamicStatePricingBlockGasLimit;
        (Block block, Transaction firstTx) = PrepareTx(Activation, 100_000, stateHeavyCode, value: 0, blockGasLimit: blockGasLimit);

        TestAllTracerWithOutput tracer = CreateTracer();
        TransactionResult firstResult = _processor.Execute(
            firstTx,
            new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
            tracer);

        Assert.That(firstResult, Is.EqualTo(TransactionResult.Ok));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.GreaterThan(firstTx.BlockGasUsed),
            "The first tx should be state-gas dominated so header.gasUsed tracks the state dimension.");

        block.Header.GasUsed = Math.Max(tracer.CumulativeRegularGasUsed, tracer.GasConsumedResult.BlockStateGas);

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

        TransactionResult secondResult = _processor.Execute(
            secondTx,
            new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
            tracer);

        Assert.That(secondTxGasLimit, Is.GreaterThan(legacyRemaining));
        Assert.That(secondResult, Is.EqualTo(TransactionResult.Ok));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.LessThan(legacyRemaining));
        Assert.That(block.Header.GasUsed, Is.LessThanOrEqualTo(block.Header.GasLimit));
    }

    [Test]
    public void Eip8037_block_validation_must_allow_tx_gas_limit_above_remaining_regular_budget_when_2d_budget_fits()
    {
        byte[] stopCode = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;

        byte[] stateHeavyCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        const long blockGasLimit = DynamicStatePricingBlockGasLimit;
        const int zeroValue = 0;

        SenderRecipientAndMiner firstParticipants = new()
        {
            SenderKey = TestItem.PrivateKeyA,
            RecipientKey = TestItem.PrivateKeyB,
            MinerKey = TestItem.PrivateKeyA,
        };

        SenderRecipientAndMiner secondParticipants = new()
        {
            SenderKey = TestItem.PrivateKeyC,
            RecipientKey = TestItem.PrivateKeyD,
            MinerKey = TestItem.PrivateKeyA,
        };

        (Block block, Transaction firstTx) = PrepareTx(
            Activation,
            GasCostOf.Transaction,
            stopCode,
            senderRecipientAndMiner: firstParticipants,
            value: zeroValue,
            blockGasLimit: blockGasLimit);

        long secondTxGasLimit = blockGasLimit - GasCostOf.Transaction + 1;
        (_, Transaction secondTx) = PrepareTx(
            Activation,
            secondTxGasLimit,
            stateHeavyCode,
            senderRecipientAndMiner: secondParticipants,
            value: zeroValue,
            blockGasLimit: blockGasLimit);

        BlockExecutionContext blockExecutionContext = new(block.Header, SpecProvider.GetSpec(block.Header));
        _processor.SetBlockExecutionContext(in blockExecutionContext);

        TestAllTracerWithOutput firstTracer = CreateTracer();
        TransactionResult firstResult = _processor.Execute(firstTx, firstTracer);

        TestAllTracerWithOutput secondTracer = CreateTracer();
        TransactionResult secondResult = _processor.Execute(secondTx, secondTracer);

        Assert.That(firstResult, Is.EqualTo(TransactionResult.Ok));
        Assert.That(secondTxGasLimit, Is.GreaterThan(blockGasLimit - firstTx.BlockGasUsed),
            "The second tx exceeds the legacy remaining regular budget.");
        Assert.That(secondResult, Is.EqualTo(TransactionResult.Ok),
            "Amsterdam should admit the tx because its capped regular contribution and state contribution both fit.");
        Assert.That(secondTracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.SSetState));
        Assert.That(block.Header.GasUsed, Is.LessThanOrEqualTo(block.Header.GasLimit));
    }

    [Test]
    public void Eip8037_same_tx_selfdestruct_must_refund_created_storage_state_gas()
    {
        byte[] childInitCode = Prepare.EvmCode
            .SSTORE(0, new byte[] { 1 })
            .Op(Instruction.ADDRESS)
            .Op(Instruction.SELFDESTRUCT)
            .Done;

        byte[] factoryCode = Prepare.EvmCode
            .Create(childInitCode, UInt256.Zero)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 600_000, factoryCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero,
            "CREATE account state and created-slot state gas should both be refunded by same-tx SELFDESTRUCT.");
    }

    [Test]
    public void Eip8037_same_tx_selfdestruct_must_refund_code_deposit_state_gas()
    {
        byte[] selfDestructRuntime = Prepare.EvmCode
            .Op(Instruction.ADDRESS)
            .Op(Instruction.SELFDESTRUCT)
            .Done;
        byte[] childInitCode = Prepare.EvmCode
            .ForInitOf(selfDestructRuntime)
            .Done;
        Address createdAddress = ContractAddress.From(Recipient, 0);

        byte[] factoryCode = Prepare.EvmCode
            .Create(childInitCode, UInt256.Zero)
            .Op(Instruction.POP)
            .Call(createdAddress, 100_000)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 600_000, factoryCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero,
            "CREATE account state and code-deposit state gas should both be refunded after same-tx SELFDESTRUCT.");
        Assert.That(TestState.AccountExists(createdAddress), Is.False);
    }

    [Test]
    public void Eip8037_top_level_create_selfdestruct_must_keep_intrinsic_create_state_gas()
    {
        byte[] initCode = Prepare.EvmCode
            .Op(Instruction.ADDRESS)
            .Op(Instruction.SELFDESTRUCT)
            .Done;

        (Block block, Transaction transaction) = PrepareInitTx(Activation, 1_000_000, initCode);
        block.Header.GasLimit = DynamicStatePricingBlockGasLimit;
        TestAllTracerWithOutput tracer = CreateTracer();

        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.CreateState),
            "The top-level transaction intrinsic CREATE state gas remains in block-state accounting.");
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

        TestAllTracerWithOutput tracer = Execute(Activation, 70_000, parentCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure),
            "The parent SSTORE should run out of gas once the child CALL burns its own failed SSTORE gas.");
        Assert.That(tracer.Error, Is.EqualTo("OutOfGas"));
    }
}
