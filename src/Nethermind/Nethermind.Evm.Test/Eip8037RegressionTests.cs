// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip8037RegressionTests : VirtualMachineTestsBase
{
    private const long DynamicStatePricingBlockGasLimit = 100_000_000;
    private static readonly byte[] DefaultCreate2Salt = [0x01];

    public enum SelfDestructBeneficiaryKind
    {
        Self,
        Existing,
        Nonexistent
    }

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    private static Prepare BuildCreateFactory(byte[] initCode, UInt256 value, bool create2, byte[]? salt = null) =>
        create2
            ? Prepare.EvmCode.Create2(initCode, salt ?? DefaultCreate2Salt, value)
            : Prepare.EvmCode.Create(initCode, value);

    [Test]
    public void Eip8037_rejects_tx_when_calldata_floor_exceeds_tx_max_execution_gas()
    {
        byte[] calldata = new byte[262_000];
        Array.Fill(calldata, (byte)0xff);

        Transaction transaction = Build.A.Transaction
            .WithGasLimit(20_000_000)
            .WithGasPrice(1)
            .WithData(calldata)
            .To(Recipient)
            .SignedAndResolved(new EthereumEcdsa(SpecProvider.ChainId), SenderKey)
            .TestObject;
        (Block block, _) = PrepareTx(
            Activation,
            transaction.GasLimit,
            transaction: transaction,
            blockGasLimit: DynamicStatePricingBlockGasLimit);

        IntrinsicGas<EthereumGasPolicy> intrinsicGas = EthereumGasPolicy.CalculateIntrinsicGas(transaction, Spec);
        Assert.That(intrinsicGas.FloorGas.Value, Is.GreaterThan(Eip7825Constants.DefaultTxGasLimitCap));

        TestAllTracerWithOutput tracer = CreateTracer();
        TransactionResult result = _processor.Execute(
            transaction,
            new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
            tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False);
            Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.GasLimitBelowIntrinsicGas));
            Assert.That(TestState.GetNonce(Sender), Is.Zero);
        }
    }

    // blockGasLimit above TX_MAX_TOTAL_GAS_LIMIT so only rule 1 (not the block gas limit) can reject.
    private TransactionResult ExecuteTxWithGasLimit(ulong txGasLimit)
    {
        Transaction transaction = Build.A.Transaction
            .WithGasLimit(txGasLimit)
            .WithGasPrice(1)
            .WithValue(0)
            .To(Recipient)
            .SignedAndResolved(new EthereumEcdsa(SpecProvider.ChainId), SenderKey)
            .TestObject;
        (Block block, _) = PrepareTx(
            Activation,
            txGasLimit,
            transaction: transaction,
            blockGasLimit: Eip8037Constants.TxMaxTotalGasLimit + 1_000_000);

        return _processor.Execute(
            transaction,
            new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
            CreateTracer());
    }

    [Test]
    public void Eip8037_rejects_tx_gas_limit_above_max_total_gas_limit()
    {
        TransactionResult result = ExecuteTxWithGasLimit(Eip8037Constants.TxMaxTotalGasLimit + 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False);
            Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.GasLimitExceedsMaxTotalCap));
            Assert.That(TestState.GetNonce(Sender), Is.Zero);
        }
    }

    [Test]
    public void Eip8037_accepts_tx_gas_limit_at_max_total_gas_limit()
    {
        TransactionResult result = ExecuteTxWithGasLimit(Eip8037Constants.TxMaxTotalGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(result.EvmExceptionType, Is.EqualTo(EvmExceptionType.None));
            Assert.That(TestState.GetNonce(Sender), Is.EqualTo(1));
        }
    }

    [Test]
    public void Eip8037_soft_failed_call_refunds_spilled_new_account_state_gas_to_gas_left()
    {
        byte[] code = Prepare.EvmCode
            .CallWithValue(TestItem.AddressF, long.MaxValue, 101.Ether)
            .Op(Instruction.POP)
            .Call(Address.FromNumber(6), long.MaxValue)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 1_000_000, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        ulong precompileCallGas = 0;
        bool foundPrecompileCall = false;
        foreach (TestAllTracerWithOutput.ActionTrace action in tracer.Actions)
        {
            if (action.IsPrecompileCall && action.To == Address.FromNumber(6))
            {
                precompileCallGas = action.Gas;
                foundPrecompileCall = true;
                break;
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(foundPrecompileCall, Is.True);
            Assert.That(precompileCallGas, Is.EqualTo(929_798));
        }
    }

    [Test]
    public void Eip8037_nested_exceptional_halts_burn_spilled_state_gas()
    {
        // Byte-exact reproduction of the recursive contract from nethermind#12964. Every frame:
        //   PUSH0 PUSH0                    ; zeros reserved for the trailing CALL
        //   PUSH1 0x0b SLOAD NOT           ; value = ~storage[11]
        //   PUSH1 0x0b DUP2 DUP2 SSTORE    ; storage[11] = value, alternating 0 <-> 2^256-1
        //                                  ; across nesting depths: fresh non-zero sets spill
        //                                  ; their state charge when the reservoir is empty,
        //                                  ; restorations to the original zero credit it back
        //   CALLDATASIZE DUP2 DUP2         ; small constants reused as call operands
        //   ADDRESS ADDRESS DELEGATECALL   ; nest one frame deeper
        //   DUP4 CALLER CALLER CALL        ; trailing CALL reuses the all-ones value as a
        //                                  ; memory-size operand, so its expansion overflows
        //                                  ; and the frame halts exceptionally on unwind
        byte[] code = Convert.FromHexString("5f5f600b5419600b8181553681813030f4833333f1");
        const ulong gasLimit = 200_000;

        TestAllTracerWithOutput tracer = Execute(
            Activation,
            gasLimit,
            code,
            blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(tracer.Error, Is.EqualTo(nameof(EvmExceptionType.OutOfGas)));
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
            Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(gasLimit));
            AssertStorage(new StorageCell(Recipient, 11), UInt256.Zero);
        }
    }

    [Test]
    public void Eip8037_top_level_halt_refunds_reservoir_funded_new_account_state_gas()
    {
        // Empty BLAKE2F input fails after the value transfer charges NEW_ACCOUNT from the reservoir.
        Address precompile = Blake2FPrecompile.Address;
        ulong gasLimit = Eip7825Constants.DefaultTxGasLimitCap + GasCostOf.NewAccountState;
        Transaction transaction = Build.A.Transaction
            .WithTo(precompile)
            .WithGasLimit(gasLimit)
            .WithGasPrice(1)
            .WithValue(1)
            .SignedAndResolved(new EthereumEcdsa(SpecProvider.ChainId), SenderKey)
            .TestObject;
        (Block block, _) = PrepareTx(
            Activation,
            gasLimit,
            transaction: transaction,
            blockGasLimit: DynamicStatePricingBlockGasLimit);

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            // A failed top-level precompile surfaces as PrecompileOutOfGasException (VirtualMachine.ExecutePrecompile
            // maps any failed precompile result to it), so the tracer reports OutOfGas.
            Assert.That(tracer.Error, Is.EqualTo(nameof(EvmExceptionType.OutOfGas)));
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(Eip7825Constants.DefaultTxGasLimitCap));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
            Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(Eip7825Constants.DefaultTxGasLimitCap));
            Assert.That(TestState.AccountExists(precompile), Is.False);
        }
    }

    [Test]
    public void Eip8037_reservoir_funded_sstore_burns_on_halt_while_unspent_reservoir_refunds()
    {
        // Gas above the EIP-7825 cap funds the reservoir with exactly one SSTORE_SET state charge;
        // the contract spends it on an SSTORE and then INVALIDs. The halt restores the reservoir to
        // its frame-entry value: the reservoir-funded write burns with the halt and the user is
        // charged only the execution dimension (the capped amount).
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .Op(Instruction.INVALID)
            .Done;

        ulong gasLimit = Eip7825Constants.DefaultTxGasLimitCap + (ulong)GasCostOf.SSetState;
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(tracer.Error, Is.EqualTo(nameof(EvmExceptionType.BadInstruction)));
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(Eip7825Constants.DefaultTxGasLimitCap));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
            AssertStorage(new StorageCell(Recipient, 1), UInt256.Zero);
        }
    }

    [Test]
    public void Eip8037_failed_create_credit_does_not_survive_top_level_halt()
    {
        // The failing inner CREATE refunds its NEW_ACCOUNT state charge to this frame's reservoir;
        // a subsequent exceptional halt must burn that credit instead of letting it reduce spent gas.
        byte[] code = Prepare.EvmCode
            .Create(Prepare.EvmCode.Op(Instruction.INVALID).Done, UInt256.Zero)
            .Op(Instruction.POP)
            .Op(Instruction.INVALID)
            .Done;

        const ulong gasLimit = 200_000;
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(tracer.Error, Is.EqualTo(nameof(EvmExceptionType.StateGasSettlementFailed)));
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        }
    }

    /// <summary>
    /// The state gas of a nested CREATE/CREATE2 never draws from the frame's execution gas: the
    /// creation succeeds in the EVM, and the transaction pays for it at the end. A transaction
    /// that cannot pay fails as a whole, so no contract can catch the shortage.
    /// </summary>
    [TestCase(false, 300_000UL, false, TestName = "Eip8037_nested_create_state_gas_not_settled_fails_tx_CREATE")]
    [TestCase(true, 300_000UL, false, TestName = "Eip8037_nested_create_state_gas_not_settled_fails_tx_CREATE2")]
    [TestCase(false, 2_000_000UL, true, TestName = "Eip8037_nested_create_state_gas_settled_CREATE")]
    [TestCase(true, 2_000_000UL, true, TestName = "Eip8037_nested_create_state_gas_settled_CREATE2")]
    public void Eip8037_nested_create_state_gas_is_settled_at_transaction_end(bool create2, ulong gasLimit, bool settles)
    {
        byte[] initCode = Prepare.EvmCode
            .PushData(256)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        // Factory code: CREATE/CREATE2(value=0, initCode), then RETURN the result (address or 0)
        byte[] factoryCode = BuildCreateFactory(initCode, UInt256.Zero, create2)
            // Stack: [address or 0]
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, factoryCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(settles ? StatusCode.Success : StatusCode.Failure));
            Assert.That(tracer.GasConsumedResult.BlockStateGas,
                Is.EqualTo(settles ? GasCostOf.CreateState + GasCostOf.CodeDepositState * 256 : 0));
            if (settles)
            {
                Assert.That(tracer.ReturnValue.IsZero(), Is.False, "the nested creation succeeds");
            }
            else
            {
                Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit), "a settlement failure consumes gas_left");
            }
        }
    }

    [TestCase(false, TestName = "Eip8037_nested_create_code_deposit_failure_must_refund_parent_create_state_CREATE")]
    [TestCase(true, TestName = "Eip8037_nested_create_code_deposit_failure_must_refund_parent_create_state_CREATE2")]
    public void Eip8037_nested_create_code_deposit_failure_must_refund_parent_create_state(bool create2)
    {
        // EIP-3541: code that starts with 0xEF fails the deposit.
        byte[] childInitCode = Prepare.EvmCode
            .PushData(0xef)
            .PushData(0)
            .Op(Instruction.MSTORE8)
            .PushData(33_000)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        byte[] code = BuildCreateFactory(childInitCode, UInt256.Zero, create2)
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
    /// Failed top-level CREATE execution burns spilled state gas, but reverted state growth must not
    /// contribute to block_state_gas_used.
    /// </summary>
    [Test]
    public void Eip8037_code_deposit_failure_burns_spilled_state_gas_without_block_state_gas()
    {
        // Initcode:
        //   1) CALL a dead account with value=1 - charges NewAccountState
        //   2) RETURN 33,000 bytes - exceeds MaxCodeSizeEip7954 (32,768), triggers deposit failure
        byte[] initCode = Prepare.EvmCode
            .CallWithValue(TestItem.AddressC, 20_000, UInt256.One)
            .PushData(33_000)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        ulong gasLimit = 1_000_000ul;

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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure),
                "CREATE tx with oversized code return should fail");
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit),
                "The top-level CREATE and initcode CALL state gas spills are burned by the exceptional halt.");
            Assert.That(tracer.GasConsumedResult.BlockStateGas,
                Is.Zero,
                "Reverted state gas must not contribute to block_state_gas_used.");
            Assert.That(TestState.AccountExists(TestItem.AddressC), Is.False,
                "The initcode CALL target must be reverted together with its state gas.");
        }
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
        ulong gasLimit = Eip7825Constants.DefaultTxGasLimitCap + GasCostOf.CreateState;

        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, createFromCalldataCode, oversizedInitCode, UInt256.Zero);
        block.Header.GasLimit = DynamicStatePricingBlockGasLimit;

        TestAllTracerWithOutput tracer = CreateTracer();
        TransactionResult result = _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(TransactionResult.Ok));
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(Eip7825Constants.DefaultTxGasLimitCap));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
            Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(Eip7825Constants.DefaultTxGasLimitCap));
        }
    }

    [Test]
    public void Eip8037_top_level_create_state_oog_must_revert_value_transfer()
    {
        byte[] initCode = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;

        (Block block, Transaction transaction) = PrepareTx(
            Activation,
            1_000_000,
            initCode,
            value: 1,
            blockGasLimit: DynamicStatePricingBlockGasLimit);
        transaction.To = null;
        transaction.Data = initCode;

        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(transaction, Spec);
        transaction.GasLimit = intrinsicGas.Standard + (ulong)GasCostOf.CreateState - 1;
        block.Header.GasLimit = DynamicStatePricingBlockGasLimit;

        UInt256 senderBalanceBefore = TestState.GetBalance(Sender);
        Address contractAddress = ContractAddress.From(transaction.SenderAddress!, transaction.Nonce);

        TestAllTracerWithOutput tracer = CreateTracer();
        tracer.IsTracingAccess = true;
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(transaction.GasLimit));
            Assert.That(TestState.GetBalance(Sender), Is.EqualTo(senderBalanceBefore - transaction.GasLimit));
            Assert.That(TestState.AccountExists(contractAddress), Is.False);
            Assert.That(tracer.Actions, Has.Count.EqualTo(1));
            Assert.That(tracer.Actions[0].CallType, Is.EqualTo(ExecutionType.CREATE));
            Assert.That(tracer.Actions[0].Gas, Is.EqualTo((ulong)GasCostOf.CreateState - 1));
            // The creation charge exceeds the state-gas limit before the frame runs.
            Assert.That(tracer.ReportedActionErrors, Is.EqualTo(new[] { EvmExceptionType.StateGasSettlementFailed }));
            Assert.That(tracer.AccessReportCount, Is.EqualTo(1));
            Assert.That(tracer.AccessedAddresses, Is.EquivalentTo(new[] { transaction.SenderAddress!, contractAddress, block.Header.GasBeneficiary! }));
        }
    }

    [TestCase(false, TestName = "Eip8037_top_level_create_on_existing_target_clears_storage_without_charging_create_state_gas")]
    [TestCase(true, TestName = "Eip8037_top_level_create_on_pruned_storage_target_clears_stale_storage")]
    public void Eip8037_top_level_create_clears_storage(bool pruneTarget)
    {
        byte[] initCode = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;

        (Block block, Transaction transaction) = PrepareTx(
            Activation,
            1_000_000,
            initCode,
            value: 0,
            blockGasLimit: DynamicStatePricingBlockGasLimit);
        transaction.To = null;
        transaction.Data = initCode;

        Address contractAddress = ContractAddress.From(transaction.SenderAddress!, transaction.Nonce);
        TestState.CreateAccount(contractAddress, pruneTarget ? UInt256.Zero : (UInt256)1);
        TestState.Set(new StorageCell(contractAddress, 0), (UInt256)1);
        if (pruneTarget)
        {
            TestState.Commit(Spec, commitRoots: false);
            Assert.That(TestState.AccountExists(contractAddress), Is.False);
        }

        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(transaction, Spec);
        if (!pruneTarget)
            transaction.GasLimit = intrinsicGas.MinimalGas;
        block.Header.GasLimit = DynamicStatePricingBlockGasLimit;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            if (!pruneTarget)
                Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(intrinsicGas.MinimalGas));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(pruneTarget ? (ulong)GasCostOf.CreateState : 0));
            Assert.That(TestState.GetBalance(contractAddress), Is.EqualTo(pruneTarget ? UInt256.Zero : (UInt256)1));
            AssertStorage(new StorageCell(contractAddress, 0), UInt256.Zero);
        }
    }

    [Test]
    public void Eip8037_top_level_create_collision_must_not_charge_create_state_gas()
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
        tracer.IsTracingAccess = true;
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
            Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(gasLimit));
            Assert.That(tracer.AccessReportCount, Is.EqualTo(1));
            Assert.That(tracer.AccessedAddresses, Is.EquivalentTo(new[] { transaction.SenderAddress!, contractAddress, block.Header.GasBeneficiary! }));
        }
    }

    [Test]
    public void Eip8037_failed_top_level_code_deposit_reports_access_once()
    {
        byte[] initCode = Prepare.EvmCode
            .PushData(0xef)
            .PushData(0)
            .Op(Instruction.MSTORE8)
            .PushData(1)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        const long gasLimit = 600_000;
        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, initCode, value: 0, blockGasLimit: DynamicStatePricingBlockGasLimit);
        transaction.To = null;
        transaction.Data = initCode;
        Address contractAddress = ContractAddress.From(transaction.SenderAddress!, transaction.Nonce);

        TestAllTracerWithOutput tracer = CreateTracer();
        tracer.IsTracingAccess = true;
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(tracer.AccessReportCount, Is.EqualTo(1));
            Assert.That(tracer.AccessedAddresses, Is.EquivalentTo(new[] { transaction.SenderAddress!, contractAddress, block.Header.GasBeneficiary! }));
        }
    }

    // EIP-2929 pre-warms the recipient when the top frame is prepared; an authorization halt happens before
    // that, so nothing loads or warms `tx.To` and the reported set must stay recipient-less.
    [Test]
    public void Eip8037_authorization_oog_reports_access_without_recipient()
    {
        EthereumEcdsa ecdsa = new(SpecProvider.ChainId);
        Address authority = TestItem.AddressF;
        AuthorizationTuple authorization = ecdsa.Sign(TestItem.PrivateKeyF, SpecProvider.ChainId, TestItem.AddressC, 0);

        Transaction SignSetCode(ulong? gasLimit = null)
        {
            TransactionBuilder<Transaction> builder = Build.A.Transaction
                .WithType(TxType.SetCode)
                .WithTo(Recipient)
                .WithGasPrice(1)
                .WithAuthorizationCode(authorization);
            if (gasLimit is not null) builder = builder.WithGasLimit(gasLimit.Value);
            return builder.SignedAndResolved(ecdsa, SenderKey, true).TestObject;
        }

        IntrinsicGas<EthereumGasPolicy> intrinsicGas = EthereumGasPolicy.CalculateIntrinsicGas(SignSetCode(), Spec);
        // One gas short of the NEW_ACCOUNT state charge for the non-existent authority, so ProcessDelegations
        // fails on its first state charge.
        ulong gasLimit = intrinsicGas.Standard.Value
            + (ulong)intrinsicGas.Standard.StateReservoir
            + (ulong)GasCostOf.NewAccountState
            - 1;

        Transaction transaction = SignSetCode(gasLimit);
        (Block block, _) = PrepareTx(
            Activation,
            gasLimit,
            transaction: transaction,
            blockGasLimit: DynamicStatePricingBlockGasLimit);

        TestAllTracerWithOutput tracer = CreateTracer();
        tracer.IsTracingAccess = true;
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(Eip7702Constants.IsDelegatedCode(TestState.GetCodeSpan(authority)), Is.False);
            Assert.That(tracer.AccessReportCount, Is.EqualTo(1));
            Assert.That(tracer.AccessedAddresses, Is.EquivalentTo(new[] { transaction.SenderAddress!, authority, block.Header.GasBeneficiary! }));
        }
    }

    // The delegation target is warmed before its cold access charge fails, so the halt's report must hold it.
    [Test]
    public void Eip8037_delegated_recipient_access_oog_reports_delegation_target()
    {
        Address delegationTarget = TestItem.AddressE;
        byte[] delegationCode = [.. Eip7702Constants.DelegationHeader, .. delegationTarget.Bytes];
        (Block block, Transaction transaction) = PrepareTx(Activation, 1_000_000, delegationCode, value: 0, blockGasLimit: DynamicStatePricingBlockGasLimit);

        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(transaction, Spec);
        transaction.GasLimit = intrinsicGas.Standard + Eip8038Constants.ColdAccountAccess - 1;

        TestAllTracerWithOutput tracer = CreateTracer();
        tracer.IsTracingAccess = true;
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(tracer.ReportedActionErrors, Is.EqualTo(new[] { EvmExceptionType.OutOfGas }));
            Assert.That(tracer.AccessReportCount, Is.EqualTo(1));
            Assert.That(tracer.AccessedAddresses, Is.EquivalentTo(new[] { transaction.SenderAddress!, Recipient, delegationTarget, block.Header.GasBeneficiary! }));
        }
    }

    [Test]
    public void Eip8037_authorization_refund_excludes_existing_authority_from_block_state_gas()
    {
        EthereumEcdsa ecdsa = new(SpecProvider.ChainId);
        Address codeSource = TestItem.AddressC;
        byte[] code = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;

        TestState.CreateAccount(codeSource, 0);
        TestState.InsertCode(codeSource, code, SpecProvider.GenesisSpec);

        const long gasLimit = 1_000_000;
        Transaction transaction = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(RecipientKey.Address)
            .WithGasLimit(gasLimit)
            .WithGasPrice(1)
            .WithAuthorizationCode(ecdsa.Sign(RecipientKey, SpecProvider.ChainId, codeSource, 0))
            .SignedAndResolved(ecdsa, SenderKey, true)
            .TestObject;
        (Block block, _) = PrepareTx(
            Activation,
            gasLimit,
            transaction: transaction,
            blockGasLimit: DynamicStatePricingBlockGasLimit);

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        ulong expectedAuthorizationStateGas = GasCostOf.PerAuthBaseState;
        // Intrinsic + auth base costs plus the delegation-target access paid when calling the
        // now-delegated authority; pinned to the fixture-validated value.
        const ulong expectedPaidGas = 67006;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(expectedPaidGas));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(expectedAuthorizationStateGas));
            Assert.That(block.Header.GasUsed, Is.EqualTo(Math.Max(tracer.CumulativeExecutionGasUsed, expectedAuthorizationStateGas)));
        }
    }

    [TestCase(false, TestName = "Eip8037_authorization_oog_restores_preparation_reservoir")]
    [TestCase(true, TestName = "Eip8037_dispatch_oog_rolls_back_authorization_and_restores_preparation_reservoir")]
    public void Eip8037_preparation_oog_restores_original_gas_state(bool authorizationSucceeds)
    {
        EthereumEcdsa ecdsa = new(SpecProvider.ChainId);
        Address codeSource = TestItem.AddressC;
        AuthorizationTuple authorization = ecdsa.Sign(RecipientKey, SpecProvider.ChainId, codeSource, 0);

        Transaction template = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(Recipient)
            .WithGasPrice(1)
            .WithAuthorizationCode(authorization)
            .TestObject;
        IntrinsicGas<EthereumGasPolicy> baseIntrinsicGas = EthereumGasPolicy.CalculateIntrinsicGas(template, Spec);

        ulong accessListAddressCost = Eip8038Constants.AccessListAddressCost
            + Address.Size * Spec.GasCosts.TxDataNonZeroMultiplier * Spec.GasCosts.TotalCostFloorPerToken;
        int accessListEntryCount = checked((int)((Eip7825Constants.DefaultTxGasLimitCap - baseIntrinsicGas.Standard.Value) / accessListAddressCost));
        AccessList.Builder accessListBuilder = new();
        for (int i = 0; i < accessListEntryCount; i++)
        {
            accessListBuilder.AddAddress(Address.FromNumber((ulong)i + 1000));
        }

        AccessList accessList = accessListBuilder.Build();
        template.AccessList = accessList;
        template.IntrinsicGasMemo = null;
        IntrinsicGas<EthereumGasPolicy> accessListIntrinsicGas = EthereumGasPolicy.CalculateIntrinsicGas(template, Spec);
        ulong remainingExecutionGas = Eip7825Constants.DefaultTxGasLimitCap - accessListIntrinsicGas.Standard.Value;
        byte[] calldata = new byte[checked((int)(remainingExecutionGas / GasCostOf.TxDataZero))];

        long reservoir = authorizationSucceeds
            ? GasCostOf.PerAuthBaseState + 1_000
            : GasCostOf.PerAuthBaseState - (long)GasCostOf.TxDataZero;
        ulong gasLimit = Eip7825Constants.DefaultTxGasLimitCap + (ulong)reservoir;
        Transaction transaction = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(Recipient)
            .WithGasLimit(gasLimit)
            .WithGasPrice(1)
            .WithData(calldata)
            .WithAccessList(accessList)
            .WithAuthorizationCode(authorization)
            .SignedAndResolved(ecdsa, SenderKey, true)
            .TestObject;
        IntrinsicGas<EthereumGasPolicy> intrinsicGas = EthereumGasPolicy.CalculateIntrinsicGas(transaction, Spec);

        Assert.That(Eip7825Constants.DefaultTxGasLimitCap - intrinsicGas.Standard.Value, Is.LessThan(GasCostOf.TxDataZero));
        TestState.CreateAccount(codeSource, 0);
        TestState.InsertCode(codeSource, Prepare.EvmCode.Op(Instruction.STOP).Done, SpecProvider.GenesisSpec);
        (Block block, _) = PrepareTx(
            Activation,
            gasLimit,
            transaction: transaction,
            blockGasLimit: DynamicStatePricingBlockGasLimit);

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(Eip7825Constants.DefaultTxGasLimitCap));
            Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(Eip7825Constants.DefaultTxGasLimitCap));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
            Assert.That(TestState.GetNonce(Recipient), Is.Zero);
            Assert.That(Eip7702Constants.IsDelegatedCode(TestState.GetCodeSpan(Recipient)), Is.False);
        }
    }

    [Test]
    public void Eip8037_halted_child_spill_does_not_reduce_persistent_authorization_state_gas()
    {
        EthereumEcdsa ecdsa = new(SpecProvider.ChainId);
        Address child = TestItem.AddressC;
        Address codeSource = TestItem.AddressE;
        byte[] childCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.INVALID)
            .Done;
        byte[] delegatedCode = Prepare.EvmCode
            .Call(child, 200_000)
            .Op(Instruction.POP)
            .Op(Instruction.INVALID)
            .Done;

        TestState.CreateAccount(child, 0);
        TestState.InsertCode(child, childCode, SpecProvider.GenesisSpec);
        TestState.CreateAccount(codeSource, 0);
        TestState.InsertCode(codeSource, delegatedCode, SpecProvider.GenesisSpec);

        const long gasLimit = 1_000_000;
        Transaction transaction = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(Recipient)
            .WithGasLimit(gasLimit)
            .WithGasPrice(1)
            .WithAuthorizationCode(ecdsa.Sign(RecipientKey, SpecProvider.ChainId, codeSource, 0))
            .SignedAndResolved(ecdsa, SenderKey, true)
            .TestObject;
        (Block block, _) = PrepareTx(
            Activation,
            gasLimit,
            transaction: transaction,
            blockGasLimit: DynamicStatePricingBlockGasLimit);

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.PerAuthBaseState));
            Assert.That(Eip7702Constants.IsDelegatedCode(TestState.GetCodeSpan(Recipient)), Is.True);
        }
    }

    [Test]
    public void Eip8037_exceptional_halt_applies_calldata_floor_after_authorization_state_gas()
    {
        EthereumEcdsa ecdsa = new(SpecProvider.ChainId);
        Address codeSource = TestItem.AddressC;
        byte[] calldata = new byte[10_000];

        TestState.CreateAccount(codeSource, 0);
        TestState.InsertCode(codeSource, Prepare.EvmCode.Op(Instruction.INVALID).Done, SpecProvider.GenesisSpec);

        Transaction transaction = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(RecipientKey.Address)
            .WithGasLimit(1_000_000)
            .WithGasPrice(1)
            .WithData(calldata)
            .WithAuthorizationCode(ecdsa.Sign(RecipientKey, SpecProvider.ChainId, codeSource, 0))
            .SignedAndResolved(ecdsa, SenderKey, true)
            .TestObject;
        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(transaction, Spec);
        Assert.That(intrinsicGas.FloorGas, Is.GreaterThan(intrinsicGas.Standard));
        transaction.GasLimit = intrinsicGas.MinimalGas;

        (Block block, _) = PrepareTx(
            Activation,
            transaction.GasLimit,
            transaction: transaction,
            blockGasLimit: DynamicStatePricingBlockGasLimit);

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(tracer.Error, Is.EqualTo(nameof(EvmExceptionType.BadInstruction)));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.PerAuthBaseState));
            Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(intrinsicGas.FloorGas));
            Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(intrinsicGas.FloorGas));
        }
    }

    [TestCase(false, 591_456L, TestName = "Eip8037_nested_create_collision_skips_state_charge_and_burns_execution_gas_CREATE")]
    [TestCase(true, 591_456L, TestName = "Eip8037_nested_create_collision_skips_state_charge_and_burns_execution_gas_CREATE2")]
    public void Eip8037_nested_create_collision_skips_state_charge_and_burns_execution_gas(bool create2, long expectedBlockGas)
    {
        byte[] initCode = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;
        byte[] salt = [0x01];
        Address collisionAddress = create2
            ? ContractAddress.From(Recipient, salt.PadLeft(32), initCode)
            : ContractAddress.From(Recipient, 0);
        TestState.CreateAccount(collisionAddress, 0, 1);

        Prepare codeBuilder = create2
            ? Prepare.EvmCode.Create2(initCode, salt, UInt256.Zero)
            : Prepare.EvmCode.Create(initCode, UInt256.Zero);
        byte[] code = codeBuilder
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        const long gasLimit = 600_000;
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
            Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(expectedBlockGas));
            Assert.That(TestState.AccountExists(collisionAddress), Is.True);
        }
    }

    [TestCase(false, TestName = "Eip8037_nested_pruned_storage_only_target_charges_create_state_gas_CREATE")]
    [TestCase(true, TestName = "Eip8037_nested_pruned_storage_only_target_charges_create_state_gas_CREATE2")]
    public void Eip8037_nested_pruned_storage_only_target_charges_create_state_gas(bool create2)
    {
        byte[] initCode = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;
        byte[] salt = [0x01];
        Address createAddress = create2
            ? ContractAddress.From(Recipient, salt.PadLeft(32), initCode)
            : ContractAddress.From(Recipient, 0);
        TestState.CreateAccount(createAddress, 0);
        TestState.Set(new StorageCell(createAddress, 0), (UInt256)1);
        TestState.Commit(Spec, commitRoots: false);
        Assert.That(TestState.AccountExists(createAddress), Is.False);

        Prepare codeBuilder = create2
            ? Prepare.EvmCode.Create2(initCode, salt, UInt256.Zero)
            : Prepare.EvmCode.Create(initCode, UInt256.Zero);
        byte[] code = codeBuilder
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 600_000, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo((ulong)GasCostOf.CreateState));
            AssertStorage(new StorageCell(createAddress, 0), UInt256.Zero);
        }
    }

    [TestCase(false, false, true, 615_151UL, TestName = "Eip8037_failed_create_refunds_spilled_state_gas_fresh_CREATE")]
    [TestCase(false, true, true, 615_151UL, TestName = "Eip8037_failed_create_refunds_spilled_state_gas_empty_existing_CREATE")]
    [TestCase(true, false, true, 615_151UL, TestName = "Eip8037_failed_create_refunds_spilled_state_gas_fresh_CREATE2")]
    [TestCase(true, true, true, 615_151UL, TestName = "Eip8037_failed_create_refunds_spilled_state_gas_empty_existing_CREATE2")]
    [TestCase(false, false, false, 239_648UL, TestName = "Eip8037_successful_create_charges_state_gas_fresh_CREATE")]
    [TestCase(false, true, false, 239_648UL, TestName = "Eip8037_successful_create_charges_state_gas_empty_existing_CREATE")]
    [TestCase(true, false, false, 239_657UL, TestName = "Eip8037_successful_create_charges_state_gas_fresh_CREATE2")]
    [TestCase(true, true, false, 239_657UL, TestName = "Eip8037_successful_create_charges_state_gas_empty_existing_CREATE2")]
    public void Eip8037_create_state_gas_matches_reference(
        bool create2,
        bool emptyExistingTarget,
        bool createFails,
        ulong expectedGas)
    {
        Address factory = TestItem.AddressC;
        byte[] initCode = Prepare.EvmCode
            .Op(createFails ? Instruction.INVALID : Instruction.STOP)
            .Done;
        byte[] salt = [0x01];
        Address createAddress = create2
            ? ContractAddress.From(factory, salt.PadLeft(32), initCode)
            : ContractAddress.From(factory, 0);

        TestState.CreateAccount(factory, 0);
        TestState.InsertCode(
            factory,
            BuildCreateFactory(initCode, UInt256.Zero, create2, salt)
                .Op(Instruction.POP)
                .Op(Instruction.STOP)
                .Done,
            SpecProvider.GenesisSpec);

        if (emptyExistingTarget)
        {
            TestState.CreateAccount(createAddress, 0);
        }

        byte[] callerCode = Prepare.EvmCode
            .Call(factory, 600_000)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(
            Activation,
            1_000_000,
            callerCode,
            blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(expectedGas));
            Assert.That(
                tracer.GasConsumedResult.BlockStateGas,
                Is.EqualTo(createFails ? 0UL : (ulong)GasCostOf.CreateState));
        }
    }

    [TestCase(false, TestName = "Eip8037_failed_create_on_balance_only_account_continues_parent_CREATE")]
    [TestCase(true, TestName = "Eip8037_failed_create_on_balance_only_account_continues_parent_CREATE2")]
    public void Eip8037_failed_create_on_balance_only_account_continues_parent(bool create2)
    {
        byte[] initCode = Prepare.EvmCode
            .Op(Instruction.INVALID)
            .Done;
        byte[] salt = [0x01];
        Address createAddress = create2
            ? ContractAddress.From(Recipient, salt.PadLeft(32), initCode)
            : ContractAddress.From(Recipient, 0);
        TestState.CreateAccount(createAddress, 1);
        TestState.Set(new StorageCell(Recipient, 0), (UInt256)0x01);
        ulong creatorNonceBefore = TestState.GetNonce(Recipient);

        Prepare codeBuilder = create2
            ? Prepare.EvmCode.Create2(initCode, salt, UInt256.Zero)
            : Prepare.EvmCode.Create(initCode, UInt256.Zero);
        byte[] code = codeBuilder
            .Op(Instruction.POP)
            .PushData(2)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 1_000_000, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(TestState.GetNonce(Recipient), Is.EqualTo(creatorNonceBefore + 1));
            AssertStorage(new StorageCell(Recipient, 0), (UInt256)2);
        }
    }

    [TestCase(false, 503_930L, TestName = "Eip8037_failed_create_burns_child_execution_gas_CREATE")]
    [TestCase(true, 503_939L, TestName = "Eip8037_failed_create_burns_child_execution_gas_CREATE2")]
    public void Eip8037_failed_create_burns_child_execution_gas(bool create2, long expectedGas)
    {
        Address factory = TestItem.AddressC;
        byte[] initCode = Bytes.FromHexString("600062070f605260006000f3");
        byte[] salt = [0];
        Prepare factoryBuilder = create2
            ? Prepare.EvmCode.Create2(initCode, salt, UInt256.Zero)
            : Prepare.EvmCode.Create(initCode, UInt256.Zero);
        byte[] factoryCode = factoryBuilder
            .PushData(0)
            .Op(Instruction.GT)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;
        Address createAddress = create2
            ? ContractAddress.From(factory, salt.PadLeft(32).AsSpan(), initCode.AsSpan())
            : ContractAddress.From(factory, 0);

        TestState.CreateAccount(factory, 0);
        TestState.InsertCode(factory, factoryCode, SpecProvider.GenesisSpec);
        TestState.CreateAccount(createAddress, 1);

        byte[] callerCode = Prepare.EvmCode.Call(factory, 0x08929a).Done;
        (Block block, Transaction transaction) = PrepareTx(
            Activation,
            0x01000000,
            callerCode,
            value: 0,
            blockGasLimit: DynamicStatePricingBlockGasLimit);
        UInt256 senderBalanceBefore = TestState.GetBalance(Sender);

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(expectedGas));
            Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(expectedGas));
            Assert.That(senderBalanceBefore - TestState.GetBalance(Sender), Is.EqualTo((UInt256)(ulong)expectedGas));
            Assert.That(TestState.GetNonce(factory), Is.EqualTo(1ul));
            AssertStorage(new StorageCell(factory, 0), UInt256.Zero);
        }
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
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero,
            "Reverted top-level CREATE state gas should not remain after the initcode failure.");
    }

    [Test]
    public void Eip8037_top_level_halt_burns_reverted_inner_create_state_gas()
    {
        byte[] childInitCode = Prepare.EvmCode
            .Create([], UInt256.One)
            .Op(Instruction.POP)
            .PushData(0)
            .PushData(0)
            .Op(Instruction.REVERT)
            .Done;

        byte[] initCode = Prepare.EvmCode
            .Create(childInitCode, UInt256.Zero)
            .Op(Instruction.POP)
            .Op(Instruction.INVALID)
            .Done;

        const long gasLimit = 1_000_000;
        (Block block, Transaction transaction) = PrepareTx(
            Activation,
            gasLimit,
            initCode,
            value: 0,
            blockGasLimit: DynamicStatePricingBlockGasLimit);
        transaction.To = null;
        transaction.Data = initCode;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
            Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(gasLimit));
        }
    }

    [Test]
    public void Eip8037_top_level_halt_must_refund_successful_inner_create_spill()
    {
        byte[] childInitCode = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;

        byte[] initCode = Prepare.EvmCode
            .Create(childInitCode, UInt256.Zero)
            .Op(Instruction.POP)
            .Create([], UInt256.Zero)
            .Op(Instruction.POP)
            .Done;

        const long gasLimit = 300_000;
        (Block block, Transaction transaction) = PrepareTx(
            Activation,
            gasLimit,
            initCode,
            value: 0,
            blockGasLimit: DynamicStatePricingBlockGasLimit);
        transaction.To = null;
        transaction.Data = initCode;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(tracer.Error, Is.EqualTo(nameof(EvmExceptionType.StateGasSettlementFailed)));
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
            Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(gasLimit));
        }
    }

    private static TestCaseData[] FailedCreateRegressionCases =>
    [
        new TestCaseData(
            "block 957 failed CREATE",
            "7f7c76141f109a1bbb8998d25ee84ca60784fba5d5138673919b039ef3abb417c07f" +
            "00000000000000000000000000000000000000000000000000000000000088485b" +
            "364875973d492183f04aa70fa45a31fcd5e822735914c6e0416202ffff16816202" +
            "ffff1691508261ffff169250376202ffff1654435b11907f0000000000000000000" +
            "00000000000001a0111ea397fe69a4b1ba7b6434bacd76000527f64774b84f385" +
            "12bf6730d2a0f6b0f6241eabfffeb153ffffb9feffffffffaaac6020527f0000" +
            "0000000000000000000000000000000000000000000000000000000000006040" +
            "527f000000000000000000000000000000000000000000000000000000000000" +
            "0000606052610100608060806000600060115af160805160a05160c05160e051" +
            "610100516101205161014051610160515b315f6161cb5b5b6202ffff165430676" +
            "83726a51be8d52d586941f5185409169aacafa861dfdbe6005b166202ffff168" +
            "161ffff169150fd5f7847d784f8797a8859a8703f11135305c3f72598a7419c" +
            "6ac0af4997603f161a5836045b826202ffff1692508361ffff169350846202ff" +
            "ff1694508561ffff169550fa030365037b3d3c3e236202ffff16534aff826202" +
            "ffff1692508361ffff169350846202ffff1694508561ffff169550fa00",
            1_000_000UL,
            0,
            1_000_000UL)
            .SetName("Eip8037_block_957_failed_create_burns_spilled_create_state_gas"),
        new TestCaseData(
            "block 1194 failed CREATE",
            "7f7c76141f109a1bbb8998d25ee84ca60784fba5d5138673919b039ef3abb417c07f" +
            "000000000000000000000000000000000000000000000000000000000000b249601" +
            "03d38805f5f397f630000007e5679aa6e40f2063e1e64192a97465b9fa65ab5ef" +
            "4222c8a501e8e25f52637c909564905f5ff55f5f5f5f5f855af15b60546108e860" +
            "8038805f5f397f63000000be5679f7a4cd19fbdf54ce586f2dfb4022e28b7f018" +
            "99f12f35156d15f526394d66496905f5ff55f5f5f5f845af45b367f30644e72e13" +
            "1a029b85045b68181585d97816a916871ca8d3c208c16d87d00186000527f30644e" +
            "72e131a029b85045b68181585d97816a916871ca8d3c208c16d87d00256020527fa" +
            "f9cf1dbaf297ea537d2d3ec9e4e87703525e0770dc0da799b77e6b9862bee5860" +
            "40526040606060606000600060075af160605160805171f586bae9c5708294d29a" +
            "066ff262c19b732c5b7f0000000000000000000000000000000000000000000000" +
            "000000000000000000016000527f000000000000000000000000000000000000000" +
            "00000000000000000000000026020527f0000000000000000000000000000000000" +
            "0000000000000000000000000000016040527f0000000000000000000000000000" +
            "00000000000000000000000000000000026060526040608060806000600060065a" +
            "f160805160a05100",
            1_000_000UL,
            0xe7f2,
            1_000_000UL)
            .SetName("Eip8037_block_1194_failed_create_burns_spilled_create_state_gas"),
        new TestCaseData(
            "block 2367 failed CREATE",
            "7f7c76141f109a1bbb8998d25ee84ca60784fba5d5138673919b039ef3abb417c07f" +
            "000000000000000000000000000000000000000000000000000000000001a76560" +
            "c65b7ab2d270ff19a070ac1de11ed3230b711ee2f82bea9fb1a928d70b7c5b5b327" +
            "e3605c632cb571258a2c6f0ae825be0bb90e6981313b37839d039db929193e7651" +
            "15aa34aa8df345b875b045b6202ffff1655156cb838efd91cf623a4f97ab7258b3" +
            "8805f5f397f63000000d8567912a8aa684c0bd1cef4b986362820f58ea59ccabfa" +
            "2fca786285f525f5ff05f5f5f5f845af45b5b7f000000000000000000000000000" +
            "00000000000000000000000000000000000016000527f0000000000000000000000" +
            "0000000000000000000000000000000000000000026020527ffd9b0e805792c921" +
            "421d5124bb834e7182882bed5dd078181d157944ed9cfeea604052604060606060" +
            "6000600060075af1606051608051615f23604c61461b5a438744471b5b5b97437f" +
            "e4d40ebec943bbf48d2dba0d8b2b8d31ade38f680cb6bc9bf20a5225c1305db2" +
            "0b61006257816202ffff169150826202ffff1692508361ffff1693503c826202ff" +
            "ff1692508361ffff169350846202ffff1694508561ffff169550fa3b3261b5bd60" +
            "df6202ffff168161ffff169150a260a35900",
            1_000_000UL,
            0xf8cc,
            1_000_000UL)
            .SetName("Eip8037_block_2367_failed_create_burns_spilled_create_state_gas"),
    ];

    [TestCaseSource(nameof(FailedCreateRegressionCases))]
    public void Eip8037_failed_create_burns_spilled_create_state_gas(
        string scenario,
        string initCodeHex,
        ulong gasLimit,
        int value,
        ulong expectedGasUsed)
    {
        byte[] initCode = Bytes.FromHexString(initCodeHex);

        (Block block, Transaction transaction) = PrepareTx(
            Activation,
            gasLimit,
            initCode,
            value: value,
            blockGasLimit: DynamicStatePricingBlockGasLimit);
        transaction.To = null;
        transaction.Data = initCode;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure), scenario);
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(expectedGasUsed), scenario);
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero, scenario);
            Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(expectedGasUsed), scenario);
        }
    }

    [Test]
    public void Eip8037_block_15149_failed_create_must_not_keep_inner_create_refund_in_state_dimension()
    {
        byte[] initCode = Bytes.FromHexString(
            "7f05558dabac9fc072364593435c5eb80848fef8caa932ac512d5ecd122ec6bba67f" +
            "00000000000000000000000000000000000000000000000000000000000d6a1e5b7f" +
            "000000000000000000000000000000b29801d0cf03fc861ad824aa1b93fc26000527" +
            "f1466f4b82c3a3c0bee6bd718e6588323f9a0a8432ea8ad32f1636d68a9c72b5a60" +
            "20526080604060406000600060105af160405160605160805160a05161078a72e493" +
            "dae2d23df83e60dba8223aa12e772557b23d6026601760ce448c474b6202ffff165c" +
            "827d2517a2f9adc9cf6d378a23164876aaad6688943ad2aa69284a09839aa6c638" +
            "805f5f397f63000001255679f3a59e0ab9b74ed342471f42902651fda5a93b83f6" +
            "ac22b8985f52633eef874e905f5ff55f5f5f5f845af45b621fe802606938805f5f" +
            "397f630000015f5679e51bfa36ea9722169a830d7c0c0ee947d36fae48afba3dbc" +
            "ab5f525f5ff05f5f5f5f5f855af15b595b60a7602277c35c7b8814a6cb4a9d1e29" +
            "78a27b5f6072b9a0b1868f8c1d63d260e76a60f286660769d3e14701ae33598a14" +
            "5b6202ffff168161ffff169150f330365b06835b3546f5845b62cf50d27357b2b3b" +
            "89ee8ddf27acd20499861e7d6e79746285a61ac50606660373de6005b075b70e5eb" +
            "93590f38ebc61dbdd26f034d11d68a00");

        const long gasLimit = 1_000_000;
        (Block block, Transaction transaction) = PrepareTx(
            Activation,
            gasLimit,
            initCode,
            value: 0,
            blockGasLimit: DynamicStatePricingBlockGasLimit);
        transaction.To = null;
        transaction.Data = initCode;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(gasLimit));
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

    [Test]
    public void Eip8037_create_state_refund_helpers_are_disabled_before_amsterdam()
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

        const long gasLimit = 500_000;
        TestAllTracerWithOutput tracer = Execute(MainnetSpecProvider.PragueActivation, gasLimit, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(493_018));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
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
        Assert.That(TestState.GetNonce(TestItem.AddressC), Is.EqualTo(0ul));
        Assert.That(TestState.AccountExists(createdAddress), Is.False);
    }

    [TestCase(false, TestName = "Eip8037_static_create_followed_by_parent_sstore_must_not_leak_create_state_gas_CREATE")]
    [TestCase(true, TestName = "Eip8037_static_create_followed_by_parent_sstore_must_not_leak_create_state_gas_CREATE2")]
    public void Eip8037_static_create_followed_by_parent_sstore_must_not_leak_create_state_gas(bool create2)
    {
        Address createdAddress = SetupStaticCreateAttempt(create2);
        TestState.Set(new StorageCell(Recipient, 0), new UInt256((ReadOnlySpan<byte>)[0xDE, 0xAD], isBigEndian: true));

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
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(344_534));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(258_230));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.SSetState));
        AssertStorage(UInt256.Zero, UInt256.Zero);
        AssertStorage(UInt256.One, UInt256.One);
        Assert.That(TestState.GetNonce(TestItem.AddressC), Is.EqualTo(0ul));
        Assert.That(TestState.AccountExists(createdAddress), Is.False);
    }

    [Test]
    public void Eip8037_delegatecall_sstore_restoration_refund_credits_local_reservoir()
    {
        byte[] childCode = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .PushData(0)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .Create([], UInt256.Zero)
            .Op(Instruction.POP)
            .PushData(1)
            .PushData(2)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 0);
        TestState.InsertCode(TestItem.AddressC, childCode, SpecProvider.GenesisSpec);

        byte[] parentCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .PushData(1)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .DelegateCall(TestItem.AddressC, 400_000)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 487_640, parentCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.CreateState + GasCostOf.SSetState));
        Assert.That(TestState.GetNonce(Recipient), Is.EqualTo(1ul));
        AssertStorage(new StorageCell(Recipient, 0), UInt256.Zero);
        AssertStorage(new StorageCell(Recipient, 1), UInt256.Zero);
        AssertStorage(new StorageCell(Recipient, 2), UInt256.One);
    }

    /// <summary>
    /// Clearing a slot that was zero at the start of the transaction does not return its state gas to
    /// gas_left, whether the frame clears it itself or a child frame clears it.
    /// </summary>
    [Test]
    public void Eip8037_state_gas_refill_does_not_raise_gas_left([Values] bool clearInChild)
    {
        byte[] childCode = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 0);
        TestState.InsertCode(TestItem.AddressC, childCode, SpecProvider.GenesisSpec);

        Prepare parent = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.GAS);
        if (clearInChild)
        {
            parent.DelegateCall(TestItem.AddressC, 400_000).Op(Instruction.POP);
        }
        else
        {
            parent.PushData(0).PushData(0).Op(Instruction.SSTORE);
        }

        // Stores 1 to slot 1 if gas_left after the clear is higher than before it.
        byte[] parentCode = parent
            .Op(Instruction.GAS)
            .Op(Instruction.GT)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 1_000_000, parentCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
            AssertStorage(new StorageCell(Recipient, 0), UInt256.Zero);
            AssertStorage(new StorageCell(Recipient, 1), UInt256.Zero);
        }
    }

    /// <summary>
    /// An intermediary that creates a slot and then REVERTs returns only its unused gas_left: the
    /// caller pays at least the pre-EIP-8037 cost of the slot, whatever <c>tx.gas</c> is.
    /// </summary>
    [Test]
    public void Eip8037_reverting_intermediary_returns_no_state_gas([Values(1_000_000UL, 20_000_000UL)] ulong gasLimit)
    {
        byte[] intermediaryCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Revert(0, 0)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 0);
        TestState.InsertCode(TestItem.AddressC, intermediaryCode, SpecProvider.GenesisSpec);

        // Stores the gas the call cost the caller to slot 0.
        byte[] callerCode = Prepare.EvmCode
            .Op(Instruction.GAS)
            .Call(TestItem.AddressC, 200_000)
            .Op(Instruction.POP)
            .Op(Instruction.GAS)
            .Op(Instruction.SWAP1)
            .Op(Instruction.SUB)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, callerCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            TestState.Get(new StorageCell(Recipient, 0), out UInt256 callCost);
            Assert.That((ulong)callCost, Is.GreaterThanOrEqualTo(GasCostOf.SSet + Eip8038Constants.ColdStorageAccess));
            AssertStorage(new StorageCell(TestItem.AddressC, 0), UInt256.Zero);
        }
    }

    /// <summary>
    /// A frame cannot create a slot with less gas_left than the slot cost before EIP-8037, so a
    /// failure handler that ran out of gas before still runs out of gas, whatever <c>tx.gas</c> is.
    /// </summary>
    [TestCase(22_105UL, 1_000_000UL, false)]
    [TestCase(22_105UL, 20_000_000UL, false)]
    [TestCase(22_106UL, 1_000_000UL, true)]
    [TestCase(22_106UL, 20_000_000UL, true)]
    public void Eip8037_new_slot_needs_legacy_execution_gas(ulong callGas, ulong gasLimit, bool succeeds)
    {
        // PUSH1 + PUSH1 + cold SSTORE of a new slot: 6 + 2,100 + 20,000.
        byte[] handlerCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 0);
        TestState.InsertCode(TestItem.AddressC, handlerCode, SpecProvider.GenesisSpec);

        byte[] callerCode = Prepare.EvmCode
            .Call(TestItem.AddressC, (long)callGas)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, callerCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage(new StorageCell(Recipient, 0), succeeds ? UInt256.One : UInt256.Zero);
            AssertStorage(new StorageCell(TestItem.AddressC, 0), succeeds ? UInt256.One : UInt256.Zero);
        }
    }

    /// <summary>
    /// The state-gas limit is <c>tx.gas</c> minus the intrinsic gas: state gas above it fails the
    /// transaction, even when a later revert would undo the state. Below it, a reverted frame's state
    /// is never paid for.
    /// </summary>
    [TestCase(400UL, false)]
    [TestCase(9UL, true)]
    public void Eip8037_state_gas_above_limit_fails_tx_even_if_reverted(ulong slots, bool succeeds)
    {
        // Creates the slots in a child frame that then reverts.
        Prepare childBuilder = Prepare.EvmCode;
        for (ulong slot = 0; slot < slots; slot++)
        {
            childBuilder.PushData(1).PushData(slot).Op(Instruction.SSTORE);
        }

        TestState.CreateAccount(TestItem.AddressC, 0);
        TestState.InsertCode(TestItem.AddressC, childBuilder.Revert(0, 0).Done, SpecProvider.GenesisSpec);

        byte[] callerCode = Prepare.EvmCode
            .Call(TestItem.AddressC, 30_000_000)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        const ulong gasLimit = 30_000_000;
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, callerCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(succeeds ? StatusCode.Success : StatusCode.Failure));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
            if (!succeeds)
            {
                // The settlement failure consumes gas_left; the gas above TX_MAX_GAS_LIMIT returns to the sender.
                Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(Eip7825Constants.DefaultTxGasLimitCap));
            }
        }
    }

    /// <summary>
    /// A transaction that cannot pay for its state from the gas it has left fails as a whole: its
    /// execution is rolled back, gas_left is consumed, and no state gas counts toward the block.
    /// </summary>
    [TestCase(1_000_000UL, false)]
    [TestCase(1_000_000UL + (ulong)GasCostOf.SSetState, true)]
    public void Eip8037_state_gas_settles_from_unused_gas(ulong extraGas, bool succeeds)
    {
        // 10 new slots: 979,200 state gas, about 221k execution gas.
        Prepare builder = Prepare.EvmCode;
        for (int slot = 0; slot < 10; slot++)
        {
            builder.PushData(1).PushData(slot).Op(Instruction.SSTORE);
        }

        byte[] code = builder.Op(Instruction.STOP).Done;
        ulong gasLimit = extraGas + 200_000;
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(succeeds ? StatusCode.Success : StatusCode.Failure));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(succeeds ? 10 * GasCostOf.SSetState : 0));
            AssertStorage(new StorageCell(Recipient, 9), succeeds ? UInt256.One : UInt256.Zero);
            if (!succeeds)
            {
                Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit));
            }
        }
    }

    [Test]
    public void Eip8037_reverted_ancestor_discards_descendant_storage_refund_credit()
    {
        byte[] descendantCode = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 0);
        TestState.InsertCode(TestItem.AddressC, descendantCode, SpecProvider.GenesisSpec);

        byte[] intermediateCode = Prepare.EvmCode
            .DelegateCall(TestItem.AddressC, 400_000)
            .Op(Instruction.POP)
            .Revert(0, 0)
            .Done;

        TestState.CreateAccount(TestItem.AddressD, 0);
        TestState.InsertCode(TestItem.AddressD, intermediateCode, SpecProvider.GenesisSpec);

        byte[] parentCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .DelegateCall(TestItem.AddressD, 600_000)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 900_000, parentCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.SSetState));
        AssertStorage(new StorageCell(Recipient, 0), UInt256.One);
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
    /// A top-level exceptional halt consumes gas_left, so the sender pays the full gas
    /// limit; BlockStateGas stays zero because the rolled-back SSTORE creates no state.
    /// </summary>
    [Test]
    public void Eip8037_top_level_exceptional_halt_charges_no_state_gas()
    {
        Prepare codeBuilder = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE);

        for (int i = 0; i < 1_025; i++)
        {
            codeBuilder.Op(Instruction.PUSH0);
        }

        const long gasLimit = 130_000;
        byte[] code = codeBuilder.Done;

        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        // The SSTORE takes no state gas from gas_left, so the PUSH0 run reaches the stack-overflow depth.
        Assert.That(tracer.Error, Is.EqualTo(nameof(EvmExceptionType.StackOverflow)));
        // The halt consumes gas_left: full gas limit, all execution.
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
    /// reservoir; the user is billed only the execution component (which already paid for
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

        const long gasLimit = 140_000;
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.LessThan(gasLimit));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        AssertStorage(new StorageCell(Recipient, 0), UInt256.Zero);
    }

    /// <summary>
    /// Same rule as the SSTORE-spill case but for an inner CALL whose NewAccountState charge
    /// spills into execution gas before the parent INVALIDs at the top level.
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
        // The inner CALL's spilled NewAccountState is burned by the top-level INVALID: full gas limit.
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

        ulong gasLimit = Eip7825Constants.DefaultTxGasLimitCap + GasCostOf.SSetState;
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(Eip7825Constants.DefaultTxGasLimitCap));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(Eip7825Constants.DefaultTxGasLimitCap));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        AssertStorage(new StorageCell(TestItem.AddressC, UInt256.Zero), UInt256.Zero);
    }

    /// <summary>
    /// Mirrors EEST <c>test_reservoir_restored_after_child_spill_and_halt</c>.
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
        // The child halt consumes its execution gas, which counts in the block execution
        // dimension; only the parent's two committed SSTOREs contribute state gas.
        Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(568_235));
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
        (Block block, Transaction firstTx) = PrepareTx(Activation, 140_000, stateHeavyCode, value: 0, blockGasLimit: blockGasLimit);

        TestAllTracerWithOutput tracer = CreateTracer();
        TransactionResult firstResult = _processor.Execute(
            firstTx,
            new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
            tracer);

        Assert.That(firstResult, Is.EqualTo(TransactionResult.Ok));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.GreaterThan(firstTx.BlockGasUsed),
            "The first tx should be state-gas dominated so header.gasUsed tracks the state dimension.");

        block.Header.GasUsed = Math.Max(tracer.CumulativeExecutionGasUsed, tracer.GasConsumedResult.BlockStateGas);

        ulong legacyRemaining = block.Header.GasLimit - block.Header.GasUsed;
        ulong secondTxGasLimit = legacyRemaining + 1ul;

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
    public void Eip8037_block_validation_must_allow_tx_gas_limit_above_remaining_execution_budget_when_2d_budget_fits()
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

        // Sized to exceed the legacy 1-D remaining budget while still fitting the 2-D check in
        // both dimensions.
        ulong secondTxGasLimit = blockGasLimit - 1000;
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
            "The second tx exceeds the legacy remaining execution budget.");
        Assert.That(secondResult, Is.EqualTo(TransactionResult.Ok),
            "Amsterdam should admit the tx because its capped execution contribution and state contribution both fit.");
        Assert.That(secondTracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.SSetState));
        Assert.That(block.Header.GasUsed, Is.LessThanOrEqualTo(block.Header.GasLimit));
    }

    [TestCase(false, TestName = "Eip8037_selfdestruct_to_new_beneficiary_without_balance_charges_no_state_gas")]
    [TestCase(true, TestName = "Eip8037_selfdestruct_to_new_beneficiary_with_balance_charges_new_account_state_gas")]
    public void Eip8037_selfdestruct_to_new_beneficiary_charges_state_gas_only_for_balance_transfer(bool fundContract)
    {
        Address beneficiary = TestItem.AddressC;
        byte[] code = Prepare.EvmCode
            .SELFDESTRUCT(beneficiary)
            .Done;

        (Block block, Transaction transaction) = PrepareTx(
            Activation,
            500_000,
            code,
            value: 0,
            blockGasLimit: DynamicStatePricingBlockGasLimit);

        if (!fundContract)
        {
            UInt256 balance = TestState.GetBalance(Recipient);
            if (!balance.IsZero)
            {
                TestState.SubtractFromBalance(Recipient, balance, SpecProvider.GenesisSpec);
            }
        }

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        ulong expectedStateGas = fundContract ? GasCostOf.NewAccountState : 0ul;

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(expectedStateGas));
        Assert.That(TestState.GetBalance(beneficiary).IsZero, Is.EqualTo(!fundContract));
    }

    [TestCase(false, TestName = "Eip8037_selfdestruct_to_new_beneficiary_charges_state_gas_when_cold")]
    [TestCase(true, TestName = "Eip8037_selfdestruct_to_new_beneficiary_charges_state_gas_when_warm")]
    public void Eip8037_selfdestruct_to_new_beneficiary_state_gas_does_not_depend_on_warmth(bool warmBeneficiary)
    {
        Address beneficiary = TestItem.AddressC;
        Prepare codeBuilder = Prepare.EvmCode;
        if (warmBeneficiary)
        {
            codeBuilder
                .PushData(beneficiary)
                .Op(Instruction.BALANCE)
                .Op(Instruction.POP);
        }

        byte[] code = codeBuilder
            .SELFDESTRUCT(beneficiary)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 500_000, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.NewAccountState));
        Assert.That(TestState.GetBalance(beneficiary), Is.EqualTo(100.Ether + UInt256.One));
    }

    [TestCase(false, 0, GasCostOf.CreateState, TestName = "Eip8037_same_tx_created_selfdestruct_to_new_beneficiary_with_zero_balance_CREATE")]
    [TestCase(false, 100, GasCostOf.CreateState + GasCostOf.NewAccountState, TestName = "Eip8037_same_tx_created_selfdestruct_to_new_beneficiary_with_balance_CREATE")]
    [TestCase(true, 0, GasCostOf.CreateState, TestName = "Eip8037_same_tx_created_selfdestruct_to_new_beneficiary_with_zero_balance_CREATE2")]
    [TestCase(true, 100, GasCostOf.CreateState + GasCostOf.NewAccountState, TestName = "Eip8037_same_tx_created_selfdestruct_to_new_beneficiary_with_balance_CREATE2")]
    public void Eip8037_same_tx_created_selfdestruct_to_new_beneficiary_keeps_created_account_state_gas(
        bool create2,
        int createdBalance,
        long expectedStateGas)
    {
        Address beneficiary = TestItem.AddressC;
        byte[] childInitCode = Prepare.EvmCode
            .SELFDESTRUCT(beneficiary)
            .Done;

        byte[] factoryCode = BuildCreateFactory(childInitCode, (UInt256)createdBalance, create2)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 800_000, factoryCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(expectedStateGas));
        Assert.That(TestState.GetBalance(beneficiary), Is.EqualTo((UInt256)createdBalance));
    }

    [Test]
    public void Eip8037_selfdestruct_to_same_tx_created_beneficiary_must_not_charge_new_account_state_again()
    {
        byte[] beneficiaryRuntime = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;
        byte[] beneficiaryInitCode = Prepare.EvmCode
            .ForInitOf(beneficiaryRuntime)
            .Done;
        Address beneficiary = ContractAddress.From(Recipient, 0);
        byte[] victimInitCode = Prepare.EvmCode
            .SELFDESTRUCT(beneficiary)
            .Done;

        byte[] factoryCode = Prepare.EvmCode
            .Create(beneficiaryInitCode, UInt256.Zero)
            .Op(Instruction.POP)
            .Create(victimInitCode, (UInt256)100)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 1_000_000, factoryCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        ulong expectedStateGas = 2ul * GasCostOf.CreateState + GasCostOf.CodeDepositState;

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(expectedStateGas));
        Assert.That(TestState.GetBalance(beneficiary), Is.EqualTo((UInt256)100));
    }

    [TestCase(false, TestName = "Eip8037_same_tx_selfdestruct_must_not_refund_created_storage_state_gas_CREATE")]
    [TestCase(true, TestName = "Eip8037_same_tx_selfdestruct_must_not_refund_created_storage_state_gas_CREATE2")]
    public void Eip8037_same_tx_selfdestruct_must_not_refund_created_storage_state_gas(bool create2)
    {
        byte[] childInitCode = Prepare.EvmCode
            .SSTORE(0, new byte[] { 1 })
            .Op(Instruction.ADDRESS)
            .Op(Instruction.SELFDESTRUCT)
            .Done;

        byte[] factoryCode = BuildCreateFactory(childInitCode, UInt256.Zero, create2)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 600_000, factoryCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.CreateState + GasCostOf.SSetState),
            "CREATE account state and created-slot state gas should not be refunded by same-tx SELFDESTRUCT.");
    }

    [TestCase(false, TestName = "Eip8037_same_tx_selfdestruct_must_not_refund_code_deposit_state_gas_CREATE")]
    [TestCase(true, TestName = "Eip8037_same_tx_selfdestruct_must_not_refund_code_deposit_state_gas_CREATE2")]
    public void Eip8037_same_tx_selfdestruct_must_not_refund_code_deposit_state_gas(bool create2)
    {
        byte[] selfDestructRuntime = Prepare.EvmCode
            .Op(Instruction.ADDRESS)
            .Op(Instruction.SELFDESTRUCT)
            .Done;
        byte[] childInitCode = Prepare.EvmCode
            .ForInitOf(selfDestructRuntime)
            .Done;
        Address createdAddress = create2
            ? ContractAddress.From(Recipient, DefaultCreate2Salt.PadLeft(32), childInitCode)
            : ContractAddress.From(Recipient, 0);

        byte[] factoryCode = BuildCreateFactory(childInitCode, UInt256.Zero, create2)
            .Op(Instruction.POP)
            .Call(createdAddress, 100_000)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 600_000, factoryCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.CreateState + (ulong)selfDestructRuntime.Length * GasCostOf.CodeDepositState),
            "CREATE account state and code-deposit state gas should not be refunded after same-tx SELFDESTRUCT.");
        Assert.That(TestState.AccountExists(createdAddress), Is.False);
    }

    [Test]
    public void Eip8037_same_tx_selfdestruct_must_not_refund_multiple_created_accounts()
    {
        byte[] childInitCode = Prepare.EvmCode
            .Op(Instruction.ADDRESS)
            .Op(Instruction.SELFDESTRUCT)
            .Done;

        byte[] factoryCode = Prepare.EvmCode
            .Create(childInitCode, UInt256.Zero)
            .Op(Instruction.POP)
            .Create(childInitCode, UInt256.Zero)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 800_000, factoryCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(2 * GasCostOf.CreateState),
            "Created-and-destroyed accounts should keep their CREATE account state gas in block-state accounting.");
    }

    [Test]
    public void Eip8037_top_level_create_selfdestruct_must_keep_intrinsic_create_state_gas_in_block_state_gas()
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
            "The destroyed top-level created account should keep its intrinsic CREATE state gas in block-state accounting.");
        Assert.That(tracer.GasConsumedResult.SpentGas - tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(GasCostOf.CreateState));
    }

    [TestCase(0UL, SelfDestructBeneficiaryKind.Self, GasCostOf.CreateState, TestName = "Eip8037_create_tx_selfdestruct_to_self_keeps_create_state_gas")]
    [TestCase(100UL, SelfDestructBeneficiaryKind.Self, GasCostOf.CreateState, TestName = "Eip8037_create_tx_selfdestruct_to_self_with_value_keeps_create_state_gas")]
    [TestCase(100UL, SelfDestructBeneficiaryKind.Existing, GasCostOf.CreateState, TestName = "Eip8037_create_tx_selfdestruct_to_existing_with_value_keeps_create_state_gas")]
    [TestCase(100UL, SelfDestructBeneficiaryKind.Nonexistent, GasCostOf.CreateState + GasCostOf.NewAccountState, TestName = "Eip8037_create_tx_selfdestruct_to_nonexistent_with_value_keeps_create_and_beneficiary_state_gas")]
    public void Eip8037_create_tx_selfdestruct_initcode_keeps_create_state_gas(
        ulong txValue,
        SelfDestructBeneficiaryKind beneficiaryKind,
        long expectedStateGas)
    {
        Address contractAddress = ContractAddress.From(Sender, 0);
        Address beneficiary = beneficiaryKind switch
        {
            SelfDestructBeneficiaryKind.Self => contractAddress,
            SelfDestructBeneficiaryKind.Existing => TestItem.AddressC,
            SelfDestructBeneficiaryKind.Nonexistent => TestItem.AddressD,
            _ => throw new ArgumentOutOfRangeException(nameof(beneficiaryKind))
        };

        if (beneficiaryKind is SelfDestructBeneficiaryKind.Existing)
        {
            byte[] code = Prepare.EvmCode
                .Op(Instruction.STOP)
                .Done;
            TestState.CreateAccount(beneficiary, UInt256.Zero);
            TestState.InsertCode(beneficiary, code, SpecProvider.GenesisSpec);
        }

        TestState.CreateAccount(Sender, 100.Ether);
        TestState.Commit(SpecProvider.GenesisSpec);

        byte[] initCode = Prepare.EvmCode
            .SELFDESTRUCT(beneficiary)
            .Done;
        Transaction transaction = Build.A.Transaction
            .WithTo(null)
            .WithGasLimit(1_000_000)
            .WithGasPrice(1)
            .WithCode(initCode)
            .WithValue((UInt256)txValue)
            .SignedAndResolved(new EthereumEcdsa(SpecProvider.ChainId), SenderKey)
            .TestObject;
        Block block = BuildBlock(Activation, SenderRecipientAndMiner.Default, transaction, DynamicStatePricingBlockGasLimit);
        TestAllTracerWithOutput tracer = CreateTracer();

        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(expectedStateGas));
        Assert.That(block.Header.GasUsed, Is.EqualTo(Math.Max(tracer.GasConsumedResult.EffectiveBlockGas, (ulong)expectedStateGas)));
    }

    [TestCase(16_777_216ul, TestName = "Eip8037_subcall_set_clear_revert_pays_no_state_gas_spill")]
    [TestCase(16_875_136ul, TestName = "Eip8037_subcall_set_clear_revert_pays_no_state_gas_reservoir")]
    public void Eip8037_subcall_set_clear_revert_pays_no_state_gas(ulong gasLimit)
    {
        byte[] childCode = Bytes.FromHexString("6001600055600060005560006000fd");

        TestState.CreateAccount(TestItem.AddressC, 0);
        TestState.InsertCode(TestItem.AddressC, childCode, SpecProvider.GenesisSpec);

        byte[] outerCode = Bytes.Concat(
            Bytes.FromHexString("6000600060006000600073"),
            TestItem.AddressC.Bytes,
            Bytes.FromHexString("5af15000"));

        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, outerCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        const long expectedExecutionGas = 46_240;
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(expectedExecutionGas));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(expectedExecutionGas));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        AssertStorage(new StorageCell(TestItem.AddressC, UInt256.Zero), UInt256.Zero);
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
            "The child SSTORE exceeds the transaction's state-gas limit, which stops execution at once.");
        Assert.That(tracer.Error, Is.EqualTo(nameof(EvmExceptionType.StateGasSettlementFailed)));
    }

    [TestCase(17_777_216ul, TestName = "Eip8037_reverting_child_restoring_parent_fresh_slot_credits_no_state_gas_reservoir_funded")]
    [TestCase(16_777_216ul, TestName = "Eip8037_reverting_child_restoring_parent_fresh_slot_credits_no_state_gas_parent_spill_funded")]
    public void Eip8037_reverting_child_restoring_parent_fresh_slot_to_pre_tx_original_must_charge_same_as_writing_a_non_original_value(ulong gasLimit)
    {
        TestAllTracerWithOutput restoresPreTxOriginal = ExecuteParentSetThenChildOverwriteAndRevert(gasLimit, TestItem.AddressC, storageSlot: 0, childWrittenValue: 0);
        TestAllTracerWithOutput writesNonOriginal = ExecuteParentSetThenChildOverwriteAndRevert(gasLimit, TestItem.AddressE, storageSlot: 1, childWrittenValue: 2);

        using (Assert.EnterMultipleScope())
        {
            AssertRollbackChargedLikeControl(restoresPreTxOriginal, writesNonOriginal,
                "both children take the identical dirty-slot SSTORE branch and then revert, so restoring the pre-tx original may only cost the same as writing a non-original value; a smaller charge means the reverted child was wrongly credited a reservoir refill for the parent-funded set");
            AssertStorage(new StorageCell(Recipient, UInt256.Zero), UInt256.One);
            AssertStorage(new StorageCell(Recipient, UInt256.One), UInt256.One);
        }
    }

    private void AssertRollbackChargedLikeControl(TestAllTracerWithOutput restoresPreTxOriginal, TestAllTracerWithOutput writesNonOriginal, string spentGasReason)
    {
        Assert.That(restoresPreTxOriginal.StatusCode, Is.EqualTo(StatusCode.Success), "the reverting child leaves the parent's committed fresh set intact, so the tx succeeds");
        Assert.That(writesNonOriginal.StatusCode, Is.EqualTo(StatusCode.Success), "the reverting child leaves the parent's committed fresh set intact, so the control tx succeeds");
        Assert.That(restoresPreTxOriginal.GasConsumedResult.SpentGas, Is.EqualTo(writesNonOriginal.GasConsumedResult.SpentGas), spentGasReason);
        Assert.That(restoresPreTxOriginal.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.SSetState),
            "the only durable state change is the parent's single fresh slot set; the reverted child adds no block state gas");
        Assert.That(writesNonOriginal.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.SSetState),
            "the control must fund its own distinct fresh slot exactly like run A; if the two runs were collapsed onto one slot the control's parent SSTORE would become a no-op reset (newSameAsCurrent) charging no state gas and this equality would fail, silently disarming the control");
    }

    private TestAllTracerWithOutput ExecuteParentSetThenChildOverwriteAndRevert(ulong gasLimit, Address childAddress, int storageSlot, int childWrittenValue, int? childOwnSlot = null)
    {
        Prepare childBuilder = Prepare.EvmCode
            .PushData(childWrittenValue)
            .PushData(storageSlot)
            .Op(Instruction.SSTORE);

        if (childOwnSlot is not null)
        {
            childBuilder = childBuilder
                .PushData(1)
                .PushData(childOwnSlot.Value)
                .Op(Instruction.SSTORE);
        }

        byte[] childCode = childBuilder
            .Revert(0, 0)
            .Done;

        TestState.CreateAccount(childAddress, 0);
        TestState.InsertCode(childAddress, childCode, SpecProvider.GenesisSpec);

        byte[] parentCode = Prepare.EvmCode
            .PushData(1)
            .PushData(storageSlot)
            .Op(Instruction.SSTORE)
            .DelegateCall(childAddress, 1_000_000)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        return Execute(Activation, gasLimit, parentCode, blockGasLimit: DynamicStatePricingBlockGasLimit);
    }

    [Test]
    public void Eip8037_reverting_child_that_consumed_its_advanced_state_gas_credit_must_reclaim_it_from_state_gas_used()
    {
        ulong gasLimit = Eip7825Constants.DefaultTxGasLimitCap;

        TestAllTracerWithOutput restoresPreTxOriginal = ExecuteParentSetThenChildOverwriteAndRevert(gasLimit, TestItem.AddressC, storageSlot: 0, childWrittenValue: 0, childOwnSlot: 2);
        TestAllTracerWithOutput writesNonOriginal = ExecuteParentSetThenChildOverwriteAndRevert(gasLimit, TestItem.AddressF, storageSlot: 1, childWrittenValue: 2, childOwnSlot: 3);

        using (Assert.EnterMultipleScope())
        {
            AssertRollbackChargedLikeControl(restoresPreTxOriginal, writesNonOriginal,
                "with reservoir 0 the child's restore of the parent's pre-tx original credits a state-gas refund that is advanced into the child reservoir, and the child's own fresh set then consumes that advance. On revert the amount>0 tail of RemoveStateGasRefundFromReservoir must reclaim the advance from state-gas-used, leaving the same charge as the control that wrote a non-original value and never credited; a smaller charge means the reverted advance leaked back as an unbilled reservoir refill");
            AssertStorage(new StorageCell(Recipient, UInt256.Zero), UInt256.One);
            AssertStorage(new StorageCell(Recipient, UInt256.One), UInt256.One);
            AssertStorage(new StorageCell(Recipient, (UInt256)2), UInt256.Zero);
            AssertStorage(new StorageCell(Recipient, (UInt256)3), UInt256.Zero);
        }
    }

    private const ulong UntrustedCallBudget = 100_000;

    /// <summary>The most net state gas a call frame with <paramref name="gas"/> may create.</summary>
    private static ulong StateGasAllowance(ulong gas) => gas * (ulong)GasCostOf.CostPerStateByte / (ulong)Eip8037Constants.LegacyGasPerStateByte;

    private static byte[] ReturnZeroCodeInitCode(int codeLength) =>
        Prepare.EvmCode.PushData(codeLength).PushData(0).Op(Instruction.RETURN).Done;

    private void InstallCode(Address address, byte[] code, UInt256 balance = default)
    {
        TestState.CreateAccount(address, balance);
        TestState.InsertCode(address, code, SpecProvider.GenesisSpec);
    }

    /// <summary>
    /// A batch calls three untrusted callees with fixed budgets and records each result. A callee that deploys a contract
    /// larger than its budget allows fails alone; the others and the transaction succeed, because a transaction with
    /// <c>tx.gas</c> at least its execution gas plus <c>CPSB / 200</c> times the forwarded budgets cannot fail settlement.
    /// </summary>
    [Test]
    public void Eip8037_state_gas_allowance_contains_malicious_callee_in_batch()
    {
        byte[] benignCode = Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done;
        // CREATE of 2,000 bytes, far above what 100k gas could deploy; INVALID if the creation failed.
        byte[] createPart = Prepare.EvmCode.Create(ReturnZeroCodeInitCode(2_000), UInt256.Zero).Done;
        int failDestination = createPart.Length + 6;
        byte[] maliciousCode =
        [
            .. createPart,
            (byte)Instruction.ISZERO, (byte)Instruction.PUSH2, (byte)(failDestination >> 8), (byte)failDestination,
            (byte)Instruction.JUMPI, (byte)Instruction.STOP, (byte)Instruction.JUMPDEST, (byte)Instruction.INVALID,
        ];
        InstallCode(TestItem.AddressC, benignCode);
        InstallCode(TestItem.AddressD, maliciousCode);
        InstallCode(TestItem.AddressE, benignCode);

        byte[] batchCode = Prepare.EvmCode
            .Call(TestItem.AddressC, (long)UntrustedCallBudget).PushData(0).Op(Instruction.SSTORE)
            .Call(TestItem.AddressD, (long)UntrustedCallBudget).PushData(1).Op(Instruction.SSTORE)
            .Call(TestItem.AddressE, (long)UntrustedCallBudget).PushData(2).Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        ulong gasLimit = 400_000 + StateGasAllowance(3 * UntrustedCallBudget);
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, batchCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage(new StorageCell(Recipient, 0), UInt256.One);
            AssertStorage(new StorageCell(Recipient, 1), UInt256.Zero);
            AssertStorage(new StorageCell(Recipient, 2), UInt256.One);
            AssertStorage(new StorageCell(TestItem.AddressC, 0), UInt256.One);
            AssertStorage(new StorageCell(TestItem.AddressE, 0), UInt256.One);
            Assert.That(TestState.AccountExists(ContractAddress.From(TestItem.AddressD, 0)), Is.False);
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(4 * GasCostOf.SSetState));
        }
    }

    /// <summary>
    /// A callee with 400k gas deploys 1,500 bytes, then creates a second contract whose own gas could deploy far more:
    /// the callee's remaining allowance limits it. 260 bytes take the allowance exactly, 261 bytes exceed it, and a slot
    /// the callee creates takes allowance until it is cleared again.
    /// </summary>
    [TestCase(260, false, false, true)]
    [TestCase(261, false, false, false)]
    [TestCase(260, true, false, false)]
    [TestCase(260, true, true, true)]
    public void Eip8037_state_gas_allowance_limits_child_by_parent_remaining(int secondCodeLength, bool setSlot, bool clearSlot, bool succeeds)
    {
        Prepare callee = Prepare.EvmCode.Create(ReturnZeroCodeInitCode(1_500), UInt256.Zero).Op(Instruction.POP);
        if (setSlot) callee.PushData(1).PushData(1).Op(Instruction.SSTORE);
        if (clearSlot) callee.PushData(0).PushData(1).Op(Instruction.SSTORE);
        callee.Create(ReturnZeroCodeInitCode(secondCodeLength), UInt256.Zero).Op(Instruction.POP).Op(Instruction.STOP);
        InstallCode(TestItem.AddressC, callee.Done);

        // Allowance 400,000 * 1,530 / 200 = 3,060,000 = 2 * 183,600 (accounts) + 1,500 * 1,530 + 260 * 1,530.
        byte[] code = Prepare.EvmCode.Call(TestItem.AddressC, 400_000).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done;
        TestAllTracerWithOutput tracer = Execute(Activation, 5_000_000, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage(new StorageCell(Recipient, 0), UInt256.One);
            Assert.That(TestState.AccountExists(ContractAddress.From(TestItem.AddressC, 0)), Is.True);
            Assert.That(TestState.AccountExists(ContractAddress.From(TestItem.AddressC, 1)), Is.EqualTo(succeeds));
        }
    }

    public enum StateCreation
    {
        Slot,
        Account,
        Contract
    }

    /// <summary>
    /// A budget that pays the execution gas of a state-creating operation never fails on the allowance: a frame with
    /// <c>g</c> gas may create the state that <c>g</c> gas could buy before EIP-8037.
    /// </summary>
    [TestCase(StateCreation.Slot, 22_106UL)]
    [TestCase(StateCreation.Account, 40_000UL)]
    [TestCase(StateCreation.Contract, 60_000UL)]
    public void Eip8037_state_gas_allowance_keeps_execution_sized_budgets(StateCreation creation, ulong budget)
    {
        byte[] calleeCode = creation switch
        {
            StateCreation.Slot => Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done,
            StateCreation.Account => Prepare.EvmCode.CallWithValue(TestItem.AddressF, 0, UInt256.One).Op(Instruction.POP).Op(Instruction.STOP).Done,
            _ => Prepare.EvmCode.Create(ReturnZeroCodeInitCode(45), UInt256.Zero).Op(Instruction.POP).Op(Instruction.STOP).Done,
        };
        InstallCode(TestItem.AddressC, calleeCode, balance: 1.Ether);

        byte[] code = Prepare.EvmCode.Call(TestItem.AddressC, (long)budget).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done;
        TestAllTracerWithOutput tracer = Execute(Activation, 1_000_000, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage(new StorageCell(Recipient, 0), UInt256.One);
        }
    }

    /// <summary>
    /// A code deposit that exceeds both the create frame's allowance and the transaction's state-gas limit fails only the
    /// CREATE: the allowance check comes first, and the rollback of the create frame removes the charge.
    /// </summary>
    /// <remarks>
    /// The top frame forwards 50,000 gas (allowance 382,500) to untrusted code, whose CREATE returns 24,576 zero bytes.
    /// The account charge (183,600) fits; the code deposit (37,601,280) exceeds the create frame's allowance and the
    /// state-gas limit of a 1,000,000 gas transaction.
    /// </remarks>
    [Test]
    public void Eip8037_allowance_check_comes_before_state_gas_limit()
    {
        byte[] untrustedCode = Prepare.EvmCode
            .Create(Bytes.FromHexString("0x6160005ff3"), UInt256.Zero)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;
        InstallCode(TestItem.AddressC, untrustedCode);

        byte[] code = Prepare.EvmCode.Call(TestItem.AddressC, 50_000).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done;
        TestAllTracerWithOutput tracer = Execute(Activation, 1_000_000, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage(new StorageCell(Recipient, 0), UInt256.One);
            Assert.That(TestState.AccountExists(ContractAddress.From(TestItem.AddressC, 0)), Is.False);
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.SSetState));
        }
    }

    /// <summary>
    /// A state-creating operation takes exactly its pre-EIP-8037 charge from gas_left: a warm new slot 20,000, a cold
    /// account-creating CALL 36,600, and a CREATE to a prefunded address 32,000. The callee budgets cover the other
    /// instructions plus exactly that charge, or one gas less.
    /// </summary>
    [TestCase(StateCreation.Slot, 3 + Eip8038Constants.ColdStorageAccess + 2 + 6 + 20_000UL, true)]
    [TestCase(StateCreation.Slot, 3 + Eip8038Constants.ColdStorageAccess + 2 + 6 + 20_000UL, false)]
    [TestCase(StateCreation.Account, 21 + 36_600UL, true)]
    [TestCase(StateCreation.Account, 21 + 36_600UL, false)]
    [TestCase(StateCreation.Contract, 9 + 32_000UL, true)]
    [TestCase(StateCreation.Contract, 9 + 32_000UL, false)]
    public void Eip8037_state_creation_takes_exact_legacy_charge(StateCreation creation, ulong exactBudget, bool enoughGas)
    {
        Address created = creation switch
        {
            StateCreation.Account => TestItem.AddressF,
            StateCreation.Contract => ContractAddress.From(TestItem.AddressC, 0),
            _ => TestItem.AddressC,
        };
        byte[] calleeCode = creation switch
        {
            // The SLOAD warms the slot, so the SSTORE creates a warm new slot.
            StateCreation.Slot => Prepare.EvmCode.PushData(0).Op(Instruction.SLOAD).Op(Instruction.POP)
                .PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done,
            StateCreation.Account => Prepare.EvmCode.CallWithValue(created, 0, UInt256.One).Op(Instruction.STOP).Done,
            _ => Prepare.EvmCode.PushData(0).PushData(0).PushData(0).Op(Instruction.CREATE).Op(Instruction.STOP).Done,
        };
        InstallCode(TestItem.AddressC, calleeCode, balance: 1.Ether);
        if (creation == StateCreation.Contract)
        {
            TestState.CreateAccount(created, UInt256.One);
        }

        ulong budget = enoughGas ? exactBudget : exactBudget - 1;
        byte[] code = Prepare.EvmCode.Call(TestItem.AddressC, (long)budget).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done;
        TestAllTracerWithOutput tracer = Execute(Activation, 1_000_000, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage(new StorageCell(Recipient, 0), enoughGas ? UInt256.One : UInt256.Zero);
            switch (creation)
            {
                case StateCreation.Slot:
                    AssertStorage(new StorageCell(created, 0), enoughGas ? UInt256.One : UInt256.Zero);
                    break;
                case StateCreation.Account:
                    Assert.That(TestState.AccountExists(created), Is.EqualTo(enoughGas));
                    break;
                default:
                    Assert.That(TestState.GetNonce(created), Is.EqualTo(enoughGas ? 1UL : 0UL));
                    break;
            }
        }
    }

    public enum Blake2FInput
    {
        Empty,
        TooFewRoundsGas,
        Valid
    }

    /// <summary>
    /// A value CALL to an active precompile without an account runs the precompile frame: when the precompile fails, on
    /// invalid input or for lack of gas, the transfer and its NEW_ACCOUNT state gas are undone.
    /// </summary>
    [TestCase(Blake2FInput.Empty, false)]
    [TestCase(Blake2FInput.TooFewRoundsGas, false)]
    [TestCase(Blake2FInput.Valid, true)]
    public void Eip8037_value_call_to_failing_precompile_charges_no_new_account(Blake2FInput input, bool succeeds)
    {
        // EIP-152 test vector 4 (0 rounds); 100,000 rounds cost more than the call stipend.
        const string validInput = "0000000048c9bdf267e6096a3ba7ca8485ae67bb2bf894fe72f36e3cf1361d5f3af54fa5d182e6ad7f520e511f6c3e2b8c68059b6bbd41fbabd9831f79217e1319cde05b61626300000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000300000000000000000000000000000001";
        byte[] callData = input switch
        {
            Blake2FInput.Empty => [],
            Blake2FInput.TooFewRoundsGas => Bytes.FromHexString("000186a0" + validInput[8..]),
            _ => Bytes.FromHexString(validInput),
        };
        Address precompile = Blake2FPrecompile.Address;

        byte[] code = Prepare.EvmCode
            .StoreDataInMemory(0, callData)
            .PushData(0).PushData(0).PushData(callData.Length).PushData(0)
            .PushData(1).PushData(precompile).PushData(0)
            .Op(Instruction.CALL)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;
        TestAllTracerWithOutput tracer = Execute(Activation, 1_000_000, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage(new StorageCell(Recipient, 0), succeeds ? UInt256.One : UInt256.Zero);
            Assert.That(TestState.AccountExists(precompile), Is.EqualTo(succeeds));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(succeeds ? GasCostOf.NewAccountState + GasCostOf.SSetState : 0));
        }
    }

    /// <summary>
    /// EIP-7778: refunds lower what the sender pays, not the block execution gas, which stays the pre-refund total. A
    /// settlement failure sets the refund counter to zero, so both are the same.
    /// </summary>
    [Test]
    public void Eip8037_block_execution_gas_is_pre_refund([Values] bool settles)
    {
        TestState.CreateAccount(Recipient, 1.Ether);
        TestState.Set(new StorageCell(Recipient, 0), UInt256.One);
        TestState.Commit(SpecProvider.GenesisSpec);

        // Clears an originally non-zero slot (STORAGE_CLEAR_REFUND), then creates a slot.
        byte[] code = Prepare.EvmCode
            .PushData(0).PushData(0).Op(Instruction.SSTORE)
            .PushData(1).PushData(1).Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;
        ulong gasLimit = settles ? 1_000_000UL : 100_000UL;
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        GasConsumed gas = tracer.GasConsumedResult;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(settles ? StatusCode.Success : StatusCode.Failure));
            Assert.That(tracer.Refund, Is.EqualTo((long)RefundOf.SClearEip8038), "refund generated during execution");
            Assert.That(gas.GasRefund, settles ? Is.GreaterThan(0UL) : Is.Zero, "refund applied");
            Assert.That(gas.BlockGas + gas.BlockStateGas, Is.EqualTo(gas.SpentGas + gas.GasRefund), "block gas is the pre-refund total");
        }
    }

    private const ulong LargeDeploymentBlockGasLimit = 500_000_000;
    private static readonly byte[] MaxCodeInitCode = ReturnZeroCodeInitCode(CodeSizeConstants.MaxCodeSizeEip7954);
    private static readonly byte[] FirstSalt = [0x01];
    private static readonly byte[] SecondSalt = [0x02];

    /// <summary>The state gas of one deployment of the maximum code size: code bytes plus the new account.</summary>
    private const long MaxCodeDeploymentStateGas = CodeSizeConstants.MaxCodeSizeEip7954 * GasCostOf.CodeDepositState + GasCostOf.CreateState;

    private void AssertMaxCodeDeployed(Address address, bool deployed) =>
        Assert.That(TestState.GetCode(address).Length, Is.EqualTo(deployed ? CodeSizeConstants.MaxCodeSizeEip7954 : 0), $"code of {address}");

    /// <summary>A contract-creation transaction deploys code of the maximum size; the top-level frame has no allowance.</summary>
    [Test]
    public void Eip8037_top_level_transaction_deploys_max_size_code()
    {
        const ulong gasLimit = 110_000_000;
        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, MaxCodeInitCode, value: 0, blockGasLimit: LargeDeploymentBlockGasLimit);
        transaction.To = null;
        transaction.Data = MaxCodeInitCode;
        Address created = ContractAddress.From(transaction.SenderAddress!, transaction.Nonce);

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(MaxCodeDeploymentStateGas));
            Assert.That(tracer.GasConsumedResult.BlockGas, Is.LessThan(1_000_000UL), "execution gas stays far below TX_MAX_GAS_LIMIT");
            AssertMaxCodeDeployed(created, deployed: true);
        }
    }

    /// <summary>
    /// A transaction calls a router, which calls a deployer that deploys code of the maximum size with CREATE2. Forwarding
    /// all gas leaves the create frame about 15M gas, an allowance of about 115M state gas. A budget for the deployer that
    /// could not buy the code bytes before EIP-8037 fails the deployment alone.
    /// </summary>
    [TestCase(long.MaxValue, true)]
    [TestCase(13_500_000L, true)]
    [TestCase(13_100_000L, false)]
    public void Eip8037_max_size_deployment_through_two_call_levels(long deployerBudget, bool deployed)
    {
        Address deployer = TestItem.AddressD;
        InstallCode(deployer, Prepare.EvmCode.Create2(MaxCodeInitCode, FirstSalt, UInt256.Zero).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        // The transaction calls the router, which calls the deployer.
        byte[] routerCode = Prepare.EvmCode.Call(deployer, deployerBudget).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done;
        TestAllTracerWithOutput tracer = Execute(Activation, 130_000_000, routerCode, blockGasLimit: LargeDeploymentBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage(new StorageCell(Recipient, 0), UInt256.One);
            AssertMaxCodeDeployed(ContractAddress.From(deployer, FirstSalt.PadLeft(32), MaxCodeInitCode), deployed);
        }
    }

    /// <summary>
    /// Two deployments of the maximum code size from one sub-call share that sub-call's allowance, so the second fails.
    /// The same deployments from two sub-calls of the top-level frame, which has no allowance, both succeed.
    /// </summary>
    [Test]
    public void Eip8037_max_size_deployments_share_the_sub_call_allowance([Values] bool separateSubCalls)
    {
        Address deployer = TestItem.AddressD;
        Prepare deployerCode = Prepare.EvmCode.Create2(MaxCodeInitCode, FirstSalt, UInt256.Zero).Op(Instruction.POP);
        if (!separateSubCalls)
        {
            deployerCode.Create2(MaxCodeInitCode, SecondSalt, UInt256.Zero).Op(Instruction.POP);
        }

        InstallCode(deployer, deployerCode.Op(Instruction.STOP).Done);
        InstallCode(TestItem.AddressE, Prepare.EvmCode.Create2(MaxCodeInitCode, SecondSalt, UInt256.Zero).Op(Instruction.POP).Op(Instruction.STOP).Done);

        Prepare router = Prepare.EvmCode.Call(deployer, long.MaxValue).PushData(0).Op(Instruction.SSTORE);
        if (separateSubCalls)
        {
            router.Call(TestItem.AddressE, long.MaxValue).PushData(1).Op(Instruction.SSTORE);
        }

        TestAllTracerWithOutput tracer = Execute(Activation, 240_000_000, router.Op(Instruction.STOP).Done, blockGasLimit: LargeDeploymentBlockGasLimit);

        Address secondDeployer = separateSubCalls ? TestItem.AddressE : deployer;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage(new StorageCell(Recipient, 0), UInt256.One);
            AssertMaxCodeDeployed(ContractAddress.From(deployer, FirstSalt.PadLeft(32), MaxCodeInitCode), deployed: true);
            AssertMaxCodeDeployed(ContractAddress.From(secondDeployer, SecondSalt.PadLeft(32), MaxCodeInitCode), deployed: separateSubCalls);
        }
    }

    public enum StateGasProbe
    {
        ChildCreatesSlot,
        ClearInFrame,
        RevertedChild
    }

    /// <summary>
    /// <c>STATEGAS</c> reports the transaction's net state gas: a sub-call that creates a slot adds <c>SSetState</c>, a
    /// clear removes it, and a reverted sub-call leaves it unchanged. Slot 0 receives the value after minus the value before.
    /// </summary>
    [TestCase(StateGasProbe.ChildCreatesSlot, GasCostOf.SSetState)]
    [TestCase(StateGasProbe.ClearInFrame, -GasCostOf.SSetState)]
    [TestCase(StateGasProbe.RevertedChild, 0L)]
    public void Eip8037_stategas_reports_net_state_gas(StateGasProbe probe, long expectedChange)
    {
        Prepare childCode = Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE);
        InstallCode(TestItem.AddressC, probe == StateGasProbe.RevertedChild ? childCode.Revert(0, 0).Done : childCode.Op(Instruction.STOP).Done);

        Prepare code = Prepare.EvmCode;
        if (probe == StateGasProbe.ClearInFrame) code.PushData(1).PushData(1).Op(Instruction.SSTORE);
        code.Op(Instruction.STATEGAS);
        if (probe == StateGasProbe.ClearInFrame)
        {
            code.PushData(0).PushData(1).Op(Instruction.SSTORE);
        }
        else
        {
            code.Call(TestItem.AddressC, 100_000).Op(Instruction.POP);
        }

        code.Op(Instruction.STATEGAS).Op(Instruction.SUB).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP);
        TestAllTracerWithOutput tracer = Execute(Activation, 1_000_000, code.Done, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage(new StorageCell(Recipient, 0), expectedChange >= 0 ? (UInt256)(ulong)expectedChange : UInt256.MaxValue - (UInt256)(ulong)(-expectedChange - 1));
        }
    }

    [Test]
    public void Eip8037_stategas_is_undefined_before_the_fork()
    {
        byte[] code = Prepare.EvmCode.Op(Instruction.STATEGAS).Op(Instruction.STOP).Done;

        TestAllTracerWithOutput tracer = Execute(MainnetSpecProvider.OsakaActivation, 100_000, code);

        Assert.That(tracer.Error, Is.EqualTo(nameof(EvmExceptionType.BadInstruction)));
    }

    /// <summary>A slot set and cleared in the same transaction refunds GAS_STORAGE_SET - WARM_ACCESS, as before EIP-8037.</summary>
    [Test]
    public void Eip8037_set_and_clear_refunds_the_new_slot_charge()
    {
        byte[] code = Prepare.EvmCode
            .PushData(1).PushData(0).Op(Instruction.SSTORE)
            .PushData(0).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 1_000_000, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(tracer.Refund, Is.EqualTo((long)(GasCostOf.SSet - GasCostOf.WarmStateRead)));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        }
    }
}
