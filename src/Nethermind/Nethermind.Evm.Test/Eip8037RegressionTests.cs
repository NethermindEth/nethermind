// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip8037RegressionTests : VirtualMachineTestsBase
{
    private const ulong DynamicStatePricingBlockGasLimit = 100_000_000;
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
    public void Eip8037_rejects_tx_when_calldata_floor_exceeds_tx_max_regular_gas()
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
            Assert.That(TestState.GetNonce(Sender), Is.EqualTo(0UL));
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
            Assert.That(precompileCallGas, Is.EqualTo(955_588));
        }
    }

    /// <summary>
    /// When a nested CREATE/CREATE2 child frame has too little regular gas to cover both
    /// the regular code deposit cost AND the state-gas spill, the create operation must fail.
    ///
    /// The child ends below the combined regular code-deposit cost and state-gas spill.
    /// Without the fix, the pre-check passes against each component separately and the
    /// charge runs on the merged parent+child pool, silently borrowing parent gas.
    /// </summary>
    [TestCase(false, TestName = "Eip8037_nested_create_code_deposit_must_not_borrow_parent_regular_gas_CREATE")]
    [TestCase(true, TestName = "Eip8037_nested_create_code_deposit_must_not_borrow_parent_regular_gas_CREATE2")]
    public void Eip8037_nested_create_code_deposit_must_not_borrow_parent_regular_gas(bool create2)
    {
        // Init code returns 256 bytes of zeros, whose code deposit (per byte: CodeDeposit regular +
        // CodeDepositState state) far exceeds the gas the child frame receives under the 63/64 rule.
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

        // The nested child's 63/64 share cannot cover the 256-byte code deposit; it must fail on
        // its own budget rather than borrowing the parent's regular gas.
        const ulong gasLimit = 300_000;
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, factoryCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        // CREATE/CREATE2 result: 0 = failure (returned in the 32-byte output)
        byte[] returnData = tracer.ReturnValue;
        using (Assert.EnterMultipleScope())
        {
            // Transaction succeeds (factory runs fine), but the nested CREATE/CREATE2 must fail
            // because the child can't afford the code deposit from its own gas alone.
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), "Factory execution should succeed");

            Assert.That(returnData.IsZero(), Is.True,
                "Nested CREATE/CREATE2 should fail: the child's 63/64 gas share cannot cover the 256-byte code deposit.");
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
            Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.GreaterThan(0));
        }
    }

    [TestCase(false, TestName = "Eip8037_nested_create_code_deposit_failure_must_refund_parent_create_state_CREATE")]
    [TestCase(true, TestName = "Eip8037_nested_create_code_deposit_failure_must_refund_parent_create_state_CREATE2")]
    public void Eip8037_nested_create_code_deposit_failure_must_refund_parent_create_state(bool create2)
    {
        byte[] childInitCode = Prepare.EvmCode
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
    /// Only the top-level CREATE state gas should remain in block_state_gas_used; the
    /// reverted initcode state growth must not contribute to the block header gas_used.
    /// </summary>
    [Test]
    public void Eip8037_code_deposit_failure_must_keep_initcode_state_gas_in_state_dimension()
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

        ulong gasLimit = 1_000_000;

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
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit - GasCostOf.NewAccountState),
                "Only the top-level CREATE intrinsic state gas is refunded (no contract created). The initcode CALL's "
                + "NewAccount state gas spilled into gas_left and is burned by the exceptional halt, so it is not refunded.");
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
    public void Eip8037_top_level_create_collision_must_still_pay_calldata_floor()
    {
        byte[] initCode = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;

        const ulong gasLimit = 600_000;
        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, initCode, value: 0, blockGasLimit: DynamicStatePricingBlockGasLimit);
        transaction.To = null;
        transaction.Data = initCode;

        Address contractAddress = ContractAddress.From(transaction.SenderAddress!, transaction.Nonce);
        TestState.CreateAccount(contractAddress, 0, 1);

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit - GasCostOf.CreateState));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
            Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(gasLimit - GasCostOf.CreateState));
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

        const ulong gasLimit = 1_000_000;
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
            Assert.That(block.Header.GasUsed, Is.EqualTo(Math.Max(tracer.CumulativeRegularGasUsed, expectedAuthorizationStateGas)));
        }
    }

    [TestCase(false, 410_397UL, TestName = "Eip8037_nested_create_collision_refunds_state_gas_and_burns_regular_gas_CREATE")]
    [TestCase(true, 410_397UL, TestName = "Eip8037_nested_create_collision_refunds_state_gas_and_burns_regular_gas_CREATE2")]
    public void Eip8037_nested_create_collision_refunds_state_gas_and_burns_regular_gas(bool create2, ulong expectedBlockGas)
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

        const ulong gasLimit = 600_000;
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
            Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(expectedBlockGas));
            Assert.That(TestState.AccountExists(collisionAddress), Is.True);
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

        const ulong gasLimit = 1_000_000;
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

        // Only the top-level create-state gas is refunded on the halt; the inner CREATEs' state
        // gas spilled into gas_left and is burned by the top-level INVALID.
        ulong refundedStateGas = GasCostOf.CreateState;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit - refundedStateGas));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
            Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(gasLimit - refundedStateGas));
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

        const ulong gasLimit = 300_000;
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
            Assert.That(tracer.Error, Is.EqualTo("OutOfGas"));
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit - GasCostOf.CreateState));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
            Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(gasLimit - GasCostOf.CreateState));
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
            0xc7510UL)
            .SetName("Eip8037_block_957_failed_create_must_not_double_refund_top_level_create_state_gas"),
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
            1_000_000UL - GasCostOf.CreateState)
            .SetName("Eip8037_block_1194_failed_create_must_exclude_top_level_create_state_gas"),
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
            0xc7510UL)
            .SetName("Eip8037_block_2367_failed_create_must_preserve_inner_reservoir_and_refund_top_level_create_state_gas"),
    ];

    [TestCaseSource(nameof(FailedCreateRegressionCases))]
    public void Eip8037_failed_create_must_exclude_top_level_create_state_gas(
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

        const ulong gasLimit = 1_000_000;
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
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(gasLimit - GasCostOf.CreateState));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(gasLimit - GasCostOf.CreateState));
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

        const ulong gasLimit = 100_000;
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

        const ulong gasLimit = 500_000;
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
        Assert.That(TestState.GetNonce(TestItem.AddressC), Is.EqualTo(0UL));
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
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(326_770));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(241_330));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(GasCostOf.SSetState));
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 0 }));
        Assert.That(TestState.Get(new StorageCell(Recipient, 1)).ToArray(), Is.EqualTo(new byte[] { 1 }));
        Assert.That(TestState.GetNonce(TestItem.AddressC), Is.EqualTo(0UL));
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
        Assert.That(TestState.GetNonce(Recipient), Is.EqualTo(1UL));
        AssertStorage(new StorageCell(Recipient, 0), UInt256.Zero);
        AssertStorage(new StorageCell(Recipient, 1), UInt256.Zero);
        AssertStorage(new StorageCell(Recipient, 2), UInt256.One);
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
    /// A top-level exceptional halt burns the spilled state gas, so the sender pays the full gas
    /// limit; BlockStateGas stays zero as the burned spill is attributed to the regular dimension.
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

        const ulong gasLimit = 130_000;
        byte[] code = codeBuilder.Done;

        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        // Under the repriced gas the PUSH0 run exhausts gas before the stack-overflow depth.
        Assert.That(tracer.Error, Is.EqualTo(nameof(EvmExceptionType.OutOfGas)));
        // The SSTORE's state gas spilled from gas_left and is burned by the halt, so the whole
        // gas limit is consumed in the regular dimension.
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

        const ulong gasLimit = 130_000;
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

        const ulong gasLimit = 600_000;
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, code, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        // The inner CALL's NewAccountState spilled into gas_left and is burned by the top-level
        // INVALID, so the sender pays the full gas limit in the regular dimension.
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
        const ulong gasLimit = 0x010092c0; // 16_815_296
        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, outerCode, blockGasLimit: DynamicStatePricingBlockGasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        // Parent's two outer SSTOREs must have landed.
        AssertStorage(new StorageCell(Recipient, 0), UInt256.One);
        AssertStorage(new StorageCell(Recipient, 1), UInt256.One);
        // Child's SSTOREs reverted on halt.
        AssertStorage(new StorageCell(TestItem.AddressC, 0), UInt256.Zero);
        AssertStorage(new StorageCell(TestItem.AddressC, 1), UInt256.Zero);
        // The child halt burns its spilled state gas, which is then attributed to the block
        // regular dimension; only the parent's two committed SSTOREs contribute state gas.
        Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(541_335));
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

        const ulong blockGasLimit = DynamicStatePricingBlockGasLimit;
        (Block block, Transaction firstTx) = PrepareTx(Activation, 130_000, stateHeavyCode, value: 0, blockGasLimit: blockGasLimit);

        TestAllTracerWithOutput tracer = CreateTracer();
        TransactionResult firstResult = _processor.Execute(
            firstTx,
            new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
            tracer);

        Assert.That(firstResult, Is.EqualTo(TransactionResult.Ok));
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.GreaterThan(firstTx.BlockGasUsed),
            "The first tx should be state-gas dominated so header.gasUsed tracks the state dimension.");

        block.Header.GasUsed = Math.Max(tracer.CumulativeRegularGasUsed, tracer.GasConsumedResult.BlockStateGas);

        ulong legacyRemaining = block.Header.GasLimit - block.Header.GasUsed;
        ulong secondTxGasLimit = legacyRemaining + 1;

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

        const ulong blockGasLimit = DynamicStatePricingBlockGasLimit;
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
            "The second tx exceeds the legacy remaining regular budget.");
        Assert.That(secondResult, Is.EqualTo(TransactionResult.Ok),
            "Amsterdam should admit the tx because its capped regular contribution and state contribution both fit.");
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

        ulong expectedStateGas = fundContract ? GasCostOf.NewAccountState : 0;

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
        ulong expectedStateGas)
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

        ulong expectedStateGas = 2 * GasCostOf.CreateState + GasCostOf.CodeDepositState;

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
        ulong expectedStateGas)
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
        Assert.That(block.Header.GasUsed, Is.EqualTo(Math.Max(tracer.GasConsumedResult.EffectiveBlockGas, expectedStateGas)));
    }

    [TestCase(16_777_216UL, TestName = "Eip8037_subcall_set_clear_revert_pays_no_state_gas_spill")]
    [TestCase(16_875_136UL, TestName = "Eip8037_subcall_set_clear_revert_pays_no_state_gas_reservoir")]
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

        const ulong expectedRegularGas = 31_340;
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(expectedRegularGas));
        Assert.That(tracer.GasConsumedResult.EffectiveBlockGas, Is.EqualTo(expectedRegularGas));
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
            "The parent SSTORE should run out of gas once the child CALL burns its own failed SSTORE gas.");
        Assert.That(tracer.Error, Is.EqualTo("OutOfGas"));
    }
}
