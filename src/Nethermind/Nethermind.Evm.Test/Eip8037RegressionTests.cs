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
    ///   regularDepositCost  = 6  (CodeDepositRegularPerWord × 1 word)
    ///   stateDepositCost    = 1174 (CostPerStateByte × 1 byte)
    ///   stateSpill          = 1174 (entire stateDepositCost spills into regular gas)
    ///   total regular needed = 6 + 1174 = 1180
    ///
    /// Child ends with 1175 regular gas after init code — 5 short.
    /// Without the fix, the pre-check passes (1175 ≥ 6 and 1175 ≥ 1174) and the
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
            .Op(Instruction.MSTORE)   // store result at memory[0]
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)   // return 32 bytes
            .Done;

        // Gas calculation:
        //   Intrinsic (CALL to existing account): 21000
        //   Factory pre-CREATE opcodes: 21 gas
        //   CREATE opcode costs:
        //     CreateRegular(9000) + InitCodeWord(2) = 9002 regular
        //     CreateState(131488) → spills entirely to regular (factory has 0 state reservoir)
        //     Total: 140490 regular
        //   Remaining after CREATE costs: 1202
        //   63/64 rule: callGas = 1202 - floor(1202/64) = 1184, factory retains 18
        //   Child: 1184 gas → 9 for init code → 1175 remaining for code deposit
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
    }

    /// <summary>
    /// BAL devnet-3 chain split repro: when a CREATE tx's initcode performs a CALL to a
    /// new (empty) account — consuming GAS_NEW_ACCOUNT (131,488) state gas — and then
    /// returns oversized code triggering a code deposit failure, the block state gas
    /// accumulator must include the state gas consumed during initcode execution, not
    /// just the intrinsic state gas.
    ///
    /// Without the fix, blockStateGas = intrinsicState (only the tx-level state gas),
    /// which is lower than the actual state gas consumed. This caused header.gasUsed to
    /// be exactly GAS_NEW_ACCOUNT lower than geth/EELS.
    /// </summary>
    [Test]
    public void Eip8037_code_deposit_failure_must_include_initcode_state_gas_in_block_accumulator()
    {
        // Initcode:
        //   1) SSTORE(slot=0, value=1) — charges SSetState (37,568) state gas
        //   2) RETURN 33,000 bytes — exceeds MaxCodeSizeEip7954 (32,768), triggers deposit failure
        //
        // The code deposit failure halts and reverts state changes, but the state gas
        // consumed during initcode execution must still count in the block accumulator.
        byte[] initCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)   // SSTORE(0, 1) → SSetState state gas
            .PushData(33_000)         // oversized: > MaxCodeSizeEip7954 (32,768)
            .PushData(0)
            .Op(Instruction.RETURN)   // return 33,000 zero bytes as deployed code
            .Done;

        long gasLimit = 1_000_000;

        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, initCode, blockGasLimit: 50_000_000);
        transaction.To = null;   // CREATE transaction
        transaction.Data = initCode;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure),
            "CREATE tx with oversized code return should fail");

        // The critical assertion: BlockStateGas must include the state gas consumed
        // during initcode execution (SSetState from the SSTORE), not just intrinsicState.
        //   intrinsicState for CREATE tx = CreateState = 131,488
        //   SSTORE in initcode adds     = SSetState = 37,568
        //   correct total               = 169,056
        // Without the fix: blockStateGas = intrinsicState = CreateState (131,488)
        // With the fix:    blockStateGas = CreateState + SSetState (169,056)
        long intrinsicStateGas = GasCostOf.CreateState;
        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.GreaterThan(intrinsicStateGas),
            $"Block state gas ({tracer.GasConsumedResult.BlockStateGas}) must exceed intrinsic state gas ({intrinsicStateGas}) " +
            $"because initcode performed an SSTORE (SSetState = {GasCostOf.SSetState})");
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
