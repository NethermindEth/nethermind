// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// End-to-end regression tests for EIP-8037 state-gas spill-refund accounting.
/// </summary>
/// <remarks>
/// Each scenario pins the expected (spentGas, blockExecutionGas, blockStateGas) triple for a
/// cross-frame refund, spill-rollback, or halt shape not covered by the fixture suite.
/// </remarks>
[TestFixture]
public class Eip8037GasAccountingTests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    private static readonly Address A = TestItem.AddressB; // tx recipient (Recipient)
    private static readonly Address P = TestItem.AddressC;
    private static readonly Address M = TestItem.AddressE;
    private static readonly Address X = TestItem.AddressF;
    private static readonly Address Dead = new("0x00000000000000000000000000000000dead0001");

    // Fresh authority (not in pre-state) so SetCode scenarios carry NEW_ACCOUNT + per-auth
    // intrinsic state gas that survives a top-level halt.
    private static readonly PrivateKey AuthorityKey =
        new("0x0000000000000000000000000000000000000000000000000000000000000042");

    public record ContractDef(Address Address, UInt256 Balance, byte[] Code);

    public record Scenario(
        string Name,
        ulong GasLimit,
        byte[] RecipientCode,
        ContractDef[] Contracts,
        bool ExpectedSuccess,
        ulong ExpectedSpentGas,
        ulong ExpectedBlockExecutionGas,
        long ExpectedBlockStateGas,
        bool IsCreate = false,
        byte[]? TxData = null,
        bool WithAuthorization = false)
    {
        public override string ToString() => Name;
    }

    // [CALLVALUE, PUSH2 d, JUMPI, <main>, JUMPDEST, <clear>] — a value call selects clear mode.
    private static byte[] BranchOnCallValue(byte[] mainSection, byte[] clearSection)
    {
        int dest = 5 + mainSection.Length;
        return Prepare.EvmCode
            .Op(Instruction.CALLVALUE)
            .Op(Instruction.PUSH2)
            .Data(new[] { (byte)(dest >> 8), (byte)dest })
            .Op(Instruction.JUMPI)
            .Data(mainSection)
            .Op(Instruction.JUMPDEST)
            .Data(clearSection)
            .Done;
    }

    private static byte[] SstoreSetThenRevert() =>
        Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Revert(0, 0).Done;

    public static IEnumerable<Scenario> Scenarios()
    {
        // Single fresh SSTORE; the state charge spills fully from gas_left (reservoir 0).
        yield return new Scenario(
            "single_sstore_spills_state_gas",
            1_000_000,
            Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done,
            [],
            ExpectedSuccess: true, ExpectedSpentGas: 125_026, ExpectedBlockExecutionGas: 27_106, ExpectedBlockStateGas: 97_920);

        // Set + clear in the same frame: the refund LIFO-refills gas_left, not the reservoir.
        yield return new Scenario(
            "set_clear_same_frame_refills_gas_left",
            1_000_000,
            Prepare.EvmCode
                .PushData(1).PushData(0).Op(Instruction.SSTORE)
                .PushData(0).PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.STOP).Done,
            [],
            ExpectedSuccess: true, ExpectedSpentGas: 21_770, ExpectedBlockExecutionGas: 27_212, ExpectedBlockStateGas: 0);

        // Tx gas above the EIP-7825 cap seeds the reservoir (R0 > 0): the SSTORE state charge
        // is reservoir-funded, no spill.
        yield return new Scenario(
            "reservoir_funded_sstore",
            20_000_000,
            Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done,
            [],
            ExpectedSuccess: true, ExpectedSpentGas: 125_026, ExpectedBlockExecutionGas: 27_106, ExpectedBlockStateGas: 97_920);

        // R0 sized below one STORAGE_SET: the charge drains the reservoir and spills the rest.
        yield return new Scenario(
            "reservoir_boundary_partial_spill",
            16_842_216,
            Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done,
            [],
            ExpectedSuccess: true, ExpectedSpentGas: 125_026, ExpectedBlockExecutionGas: 27_106, ExpectedBlockStateGas: 97_920);

        // Child halt then top-level halt with R0 > 0: the initial reservoir survives both
        // halts and is refunded (spent = gasLimit - R0).
        {
            byte[] pCode = Prepare.EvmCode
                .PushData(1).PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.INVALID).Done;
            byte[] aCode = Prepare.EvmCode
                .Call(P, 400_000).Op(Instruction.POP)
                .Op(Instruction.INVALID).Done;
            yield return new Scenario(
                "reservoir_restored_on_top_halt",
                20_000_000,
                aCode,
                [new ContractDef(P, 0, pCode)],
                ExpectedSuccess: false, ExpectedSpentGas: 16_777_216, ExpectedBlockExecutionGas: 16_777_216, ExpectedBlockStateGas: 0);
        }

        // Cross-frame refund advance fully discharged against the setter's usage: A sets a
        // slot, M re-enters A to clear it, all frames succeed.
        {
            byte[] aMain = Prepare.EvmCode
                .PushData(1).PushData(0).Op(Instruction.SSTORE)
                .Call(M, 600_000).Op(Instruction.POP)
                .Op(Instruction.STOP).Done;
            byte[] aClear = Prepare.EvmCode
                .PushData(0).PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.STOP).Done;
            byte[] mCode = Prepare.EvmCode
                .CallWithValue(A, 300_000, 1).Op(Instruction.POP)
                .Op(Instruction.STOP).Done;
            yield return new Scenario(
                "cross_frame_refund_advance_full_discharge",
                3_000_000,
                BranchOnCallValue(aMain, aClear),
                [new ContractDef(M, 1_000_000_000_000_000_000, mCode)],
                ExpectedSuccess: true, ExpectedSpentGas: 31_512, ExpectedBlockExecutionGas: 39_389, ExpectedBlockStateGas: 0);
        }

        // As above, but the top level REVERTs after the advance was discharged.
        {
            byte[] aMain = Prepare.EvmCode
                .PushData(1).PushData(0).Op(Instruction.SSTORE)
                .Call(M, 600_000).Op(Instruction.POP)
                .Revert(0, 0).Done;
            byte[] aClear = Prepare.EvmCode
                .PushData(0).PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.STOP).Done;
            byte[] mCode = Prepare.EvmCode
                .CallWithValue(A, 300_000, 1).Op(Instruction.POP)
                .Op(Instruction.STOP).Done;
            yield return new Scenario(
                "cross_frame_refund_discharge_then_top_revert",
                3_000_000,
                BranchOnCallValue(aMain, aClear),
                [new ContractDef(M, 1_000_000_000_000_000_000, mCode)],
                ExpectedSuccess: false, ExpectedSpentGas: 39_395, ExpectedBlockExecutionGas: 39_395, ExpectedBlockStateGas: 0);
        }

        // Spill from a reverted grandchild rides through the child's exceptional halt; the old
        // gross spill merge docked the parent reservoir, overcharging the sender.
        {
            byte[] pCode = Prepare.EvmCode
                .Call(X, 300_000).Op(Instruction.POP)
                .Op(Instruction.INVALID).Done;
            byte[] aCode = Prepare.EvmCode
                .Call(P, 800_000).Op(Instruction.POP)
                .Op(Instruction.STOP).Done;
            yield return new Scenario(
                "reverted_grandchild_spill_through_child_halt",
                3_000_000,
                aCode,
                [
                    new ContractDef(P, 0, pCode),
                    new ContractDef(X, 0, SstoreSetThenRevert()),
                ],
                ExpectedSuccess: true, ExpectedSpentGas: 818_023, ExpectedBlockExecutionGas: 818_023, ExpectedBlockStateGas: 0);
        }

        // Double-marking trace from the PR #12369 review: M imports spill from reverted X, gets
        // a same-frame NEW_ACCOUNT refund (soft-failed value call), returns into P, P halts.
        {
            byte[] mCode = Prepare.EvmCode
                .Call(X, 300_000).Op(Instruction.POP)
                .CallWithValue(Dead, 100_000, UInt256.Parse("1000000000000000000")).Op(Instruction.POP)
                .Op(Instruction.STOP).Done;
            byte[] pCode = Prepare.EvmCode
                .PushData(1).PushData(0).Op(Instruction.SSTORE)
                .Call(M, 600_000).Op(Instruction.POP)
                .Op(Instruction.INVALID).Done;
            byte[] aCode = Prepare.EvmCode
                .Call(P, 1_200_000).Op(Instruction.POP)
                .Op(Instruction.STOP).Done;
            yield return new Scenario(
                "double_marked_refund_through_child_halt",
                3_000_000,
                aCode,
                [
                    new ContractDef(P, 0, pCode),
                    new ContractDef(M, 0, mCode),
                    new ContractDef(X, 0, SstoreSetThenRevert()),
                ],
                ExpectedSuccess: true, ExpectedSpentGas: 1_218_023, ExpectedBlockExecutionGas: 1_218_023, ExpectedBlockStateGas: 0);

            // Same shape ending in a top-level halt: the block split keeps the intrinsic state.
            byte[] aHaltCode = Prepare.EvmCode
                .Call(P, 1_200_000).Op(Instruction.POP)
                .Op(Instruction.INVALID).Done;
            yield return new Scenario(
                "double_marked_refund_then_top_halt_with_auth",
                3_000_000,
                aHaltCode,
                [
                    new ContractDef(P, 0, pCode),
                    new ContractDef(M, 0, mCode),
                    new ContractDef(X, 0, SstoreSetThenRevert()),
                ],
                ExpectedSuccess: false, ExpectedSpentGas: 3_000_000, ExpectedBlockExecutionGas: 2_781_210, ExpectedBlockStateGas: 218_790,
                WithAuthorization: true);
        }

        // Child-halt spill then top-level halt: burned spill stays execution; block state gas
        // keeps the full intrinsic state.
        {
            byte[] pCode = Prepare.EvmCode
                .PushData(1).PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.INVALID).Done;
            byte[] aCode = Prepare.EvmCode
                .Call(P, 400_000).Op(Instruction.POP)
                .Op(Instruction.INVALID).Done;
            yield return new Scenario(
                "child_halt_spill_then_top_halt_with_auth",
                3_000_000,
                aCode,
                [new ContractDef(P, 0, pCode)],
                ExpectedSuccess: false, ExpectedSpentGas: 3_000_000, ExpectedBlockExecutionGas: 2_781_210, ExpectedBlockStateGas: 218_790,
                WithAuthorization: true);
        }

        // Partially discharged advance: P' imports spill from reverted X and clears two of P's
        // slots (advance 2*SSET), M discharges one SSET, P halts revoking the remainder.
        {
            byte[] pMain = Prepare.EvmCode
                .PushData(1).PushData(0).Op(Instruction.SSTORE)
                .PushData(1).PushData(1).Op(Instruction.SSTORE)
                .Call(M, 900_000).Op(Instruction.POP)
                .Op(Instruction.INVALID).Done;
            byte[] pClear = Prepare.EvmCode
                .Call(X, 250_000).Op(Instruction.POP)
                .PushData(0).PushData(0).Op(Instruction.SSTORE)
                .PushData(0).PushData(1).Op(Instruction.SSTORE)
                .Op(Instruction.STOP).Done;
            byte[] mCode = Prepare.EvmCode
                .PushData(1).PushData(0).Op(Instruction.SSTORE)
                .CallWithValue(P, 500_000, 1).Op(Instruction.POP)
                .Op(Instruction.STOP).Done;
            byte[] aCode = Prepare.EvmCode
                .Call(P, 1_500_000).Op(Instruction.POP)
                .Op(Instruction.STOP).Done;
            yield return new Scenario(
                "partially_discharged_advance_revoked_by_halt",
                3_000_000,
                aCode,
                [
                    new ContractDef(P, 0, BranchOnCallValue(pMain, pClear)),
                    new ContractDef(M, 1_000_000_000_000_000_000, mCode),
                    new ContractDef(X, 0, SstoreSetThenRevert()),
                ],
                ExpectedSuccess: true, ExpectedSpentGas: 1_518_023, ExpectedBlockExecutionGas: 1_518_023, ExpectedBlockStateGas: 0);

            byte[] aHaltCode = Prepare.EvmCode
                .Call(P, 1_500_000).Op(Instruction.POP)
                .Op(Instruction.INVALID).Done;
            yield return new Scenario(
                "partially_discharged_advance_then_top_halt_with_auth",
                3_000_000,
                aHaltCode,
                [
                    new ContractDef(P, 0, BranchOnCallValue(pMain, pClear)),
                    new ContractDef(M, 1_000_000_000_000_000_000, mCode),
                    new ContractDef(X, 0, SstoreSetThenRevert()),
                ],
                ExpectedSuccess: false, ExpectedSpentGas: 3_000_000, ExpectedBlockExecutionGas: 2_781_210, ExpectedBlockStateGas: 218_790,
                WithAuthorization: true);
        }

        // Advance revoked by the receiving frame's REVERT; the parent continues and succeeds.
        {
            byte[] aMain = Prepare.EvmCode
                .PushData(1).PushData(0).Op(Instruction.SSTORE)
                .Call(M, 600_000).Op(Instruction.POP)
                .PushData(1).PushData(1).Op(Instruction.SSTORE)
                .Op(Instruction.STOP).Done;
            byte[] aClear = Prepare.EvmCode
                .PushData(0).PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.STOP).Done;
            byte[] mCode = Prepare.EvmCode
                .CallWithValue(A, 300_000, 1).Op(Instruction.POP)
                .Revert(0, 0).Done;
            yield return new Scenario(
                "advance_revoked_by_revert",
                3_000_000,
                BranchOnCallValue(aMain, aClear),
                [new ContractDef(M, 1, mCode)],
                ExpectedSuccess: true, ExpectedSpentGas: 247_341, ExpectedBlockExecutionGas: 51_501, ExpectedBlockStateGas: 195_840);
        }

        // Top-level and inner CREATE state charges spill from gas_left. On the top-level OOG,
        // the rollback refills and then burns that gas as execution while block state stays zero.
        {
            byte[] childInitCode = Prepare.EvmCode.Op(Instruction.STOP).Done;
            byte[] initCode = Prepare.EvmCode
                .Create(childInitCode, UInt256.Zero)
                .Op(Instruction.POP)
                .Create([], UInt256.Zero)
                .Op(Instruction.POP)
                .Done;
            yield return new Scenario(
                "inner_create_spill_then_top_oog",
                300_000,
                [],
                [],
                ExpectedSuccess: false, ExpectedSpentGas: 300_000, ExpectedBlockExecutionGas: 300_000, ExpectedBlockStateGas: 0,
                IsCreate: true,
                TxData: initCode);
        }

        // CREATE deposit fails on invalid code (EIP-3541): the child create frame halts and
        // the parent CREATE state gas is refunded.
        {
            byte[] initCode = Prepare.EvmCode
                .PushData(0xef).PushData(0).Op(Instruction.MSTORE8)
                .PushData(1).PushData(0).Op(Instruction.RETURN)
                .Done;
            byte[] aCode = Prepare.EvmCode
                .Create(initCode, UInt256.Zero)
                .Op(Instruction.POP)
                .Op(Instruction.STOP)
                .Done;
            yield return new Scenario(
                "create_deposit_invalid_code",
                2_000_000,
                aCode,
                [],
                ExpectedSuccess: true, ExpectedSpentGas: 1_788_443, ExpectedBlockExecutionGas: 1_788_443, ExpectedBlockStateGas: 0);
        }
    }

    [TestCaseSource(nameof(Scenarios))]
    public void Eip8037_gas_accounting_matches_expected_split(Scenario scenario)
    {
        foreach (ContractDef c in scenario.Contracts)
        {
            TestState.CreateAccount(c.Address, c.Balance);
            TestState.InsertCode(c.Address, c.Code, SpecProvider.GenesisSpec);
        }

        Transaction? transaction = null;
        if (scenario.WithAuthorization)
        {
            EthereumEcdsa ecdsa = new(SpecProvider.ChainId);
            TestState.CreateAccount(Recipient, 0);
            TestState.InsertCode(Recipient, scenario.RecipientCode, SpecProvider.GenesisSpec);
            transaction = Build.A.Transaction
                .WithType(TxType.SetCode)
                .WithTo(Recipient)
                .WithGasLimit(scenario.GasLimit)
                .WithGasPrice(1)
                .WithAuthorizationCode(ecdsa.Sign(AuthorityKey, 0, P, 0))
                .SignedAndResolved(ecdsa, SenderKey, true)
                .TestObject;
        }

        (Block block, Transaction tx) = PrepareTx(
            Activation,
            scenario.GasLimit,
            scenario.WithAuthorization || scenario.IsCreate ? null : scenario.RecipientCode,
            value: 0,
            blockGasLimit: 100_000_000,
            transaction: transaction);

        if (scenario.IsCreate)
        {
            tx.To = null;
            tx.Data = scenario.TxData;
        }

        TestAllTracerWithOutput tracer = CreateTracer();
        // Access tracing would pre-warm every touched account/slot and shift all expected values.
        tracer.IsTracingAccess = false;
        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(scenario.ExpectedSuccess ? StatusCode.Success : StatusCode.Failure), "status");
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(scenario.ExpectedSpentGas), "spent gas");
            Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(scenario.ExpectedBlockExecutionGas), "block execution gas");
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(scenario.ExpectedBlockStateGas), "block state gas");
        }
    }
}

[TestFixture]
public class TransactionSettlementKernelTests
{
    [TestCase(7L, 0, 0UL, 3UL, 90UL, 10UL, TestName = "refund counter and code refund")]
    [TestCase(30L, 0, 0UL, 0UL, 80UL, 20UL, TestName = "refund is capped")]
    [TestCase(0L, 2, 7UL, 1UL, 85UL, 15UL, TestName = "destroy refund is included")]
    [TestCase(0L, 0, 0UL, 0UL, 100UL, 0UL, TestName = "zero refund")]
    [TestCase(-7L, 0, 0UL, 0UL, 107UL, 0UL, TestName = "negative refund increases gas")]
    public void Success_applies_signed_refund_and_cap(
        long refundCounter,
        int destroyCount,
        ulong destroyRefund,
        ulong codeInsertExecutionRefund,
        ulong expectedOperationGas,
        ulong expectedGasRefund)
    {
        TransactionSettlementResult result = TransactionSettlementKernel.Calculate(
            transactionGasLimit: 120,
            preRefundGas: 100,
            refundCounter,
            destroyCount,
            destroyRefund,
            codeInsertExecutionRefund,
            calldataFloorGas: 0,
            stateGasUsed: 0,
            refundQuotient: 5,
            isError: false,
            shouldRevert: false,
            isEip8037Enabled: true,
            isEip7778Enabled: true);

        AssertSettlement(
            result,
            expectedOperationGas,
            expectedOperationGas,
            blockGas: 100,
            blockStateGas: 0,
            maxUsedGas: 100,
            expectedGasRefund);
    }

    [TestCase(false, true, 100UL, 90UL, 100UL, TestName = "revert uses pre-refund gas")]
    [TestCase(true, false, 120UL, 110UL, 120UL, TestName = "legacy error uses transaction gas limit")]
    public void Revert_and_legacy_error_ignore_substate_and_destroy_refunds(
        bool isError,
        bool shouldRevert,
        ulong expectedGasUsedBeforeRefund,
        ulong expectedOperationGas,
        ulong expectedBlockGas)
    {
        TransactionSettlementResult result = TransactionSettlementKernel.Calculate(
            transactionGasLimit: 120,
            preRefundGas: 100,
            refundCounter: 1_000,
            destroyCount: 10,
            destroyRefund: 50,
            codeInsertExecutionRefund: 10,
            calldataFloorGas: 0,
            stateGasUsed: 99,
            refundQuotient: 5,
            isError,
            shouldRevert,
            isEip8037Enabled: false,
            isEip7778Enabled: true);

        AssertSettlement(
            result,
            expectedOperationGas,
            expectedOperationGas,
            expectedBlockGas,
            blockStateGas: 0,
            maxUsedGas: expectedGasUsedBeforeRefund,
            gasRefund: 10);
    }

    [TestCase(20L, 20L, 90UL, 90UL, 90UL, 100UL, TestName = "calldata floor dominates refund and execution dimension")]
    [TestCase(20L, 90L, 0UL, 80UL, 10UL, 100UL, TestName = "state dimension dominates execution dimension")]
    [TestCase(20L, 150L, 0UL, 80UL, 0UL, 100UL, TestName = "state subtraction saturates")]
    [TestCase(20L, 20L, 120UL, 120UL, 120UL, 120UL, TestName = "calldata floor dominates max-used gas")]
    public void Eip8037_projects_transaction_and_block_gas_dimensions(
        long refundCounter,
        long stateGasUsed,
        ulong calldataFloorGas,
        ulong expectedSpentGas,
        ulong expectedBlockGas,
        ulong expectedMaxUsedGas)
    {
        TransactionSettlementResult result = TransactionSettlementKernel.Calculate(
            transactionGasLimit: 150,
            preRefundGas: 100,
            refundCounter,
            destroyCount: 0,
            destroyRefund: 0,
            codeInsertExecutionRefund: 0,
            calldataFloorGas,
            stateGasUsed,
            refundQuotient: 5,
            isError: false,
            shouldRevert: false,
            isEip8037Enabled: true,
            isEip7778Enabled: true);

        AssertSettlement(
            result,
            expectedSpentGas,
            operationGas: 80,
            expectedBlockGas,
            blockStateGas: (ulong)stateGasUsed,
            expectedMaxUsedGas,
            gasRefund: 20);
    }

    [TestCase(false, 0UL, TestName = "pre-EIP-7778 has no block execution projection")]
    [TestCase(true, 110UL, TestName = "EIP-7778 projects pre-refund calldata floor")]
    public void PreEip8037_projection_preserves_Eip7778_branch(bool isEip7778Enabled, ulong expectedBlockGas)
    {
        TransactionSettlementResult result = TransactionSettlementKernel.Calculate(
            transactionGasLimit: 150,
            preRefundGas: 100,
            refundCounter: 20,
            destroyCount: 0,
            destroyRefund: 0,
            codeInsertExecutionRefund: 0,
            calldataFloorGas: 110,
            stateGasUsed: long.MaxValue,
            refundQuotient: 5,
            isError: false,
            shouldRevert: false,
            isEip8037Enabled: false,
            isEip7778Enabled);

        AssertSettlement(
            result,
            spentGas: 110,
            operationGas: 80,
            expectedBlockGas,
            blockStateGas: 0,
            maxUsedGas: 110,
            gasRefund: 20);
    }

    [Test]
    public void Maximum_values_preserve_fixed_width_refund_semantics()
    {
        TransactionSettlementResult result = TransactionSettlementKernel.Calculate(
            transactionGasLimit: ulong.MaxValue,
            preRefundGas: ulong.MaxValue,
            refundCounter: long.MaxValue,
            destroyCount: 0,
            destroyRefund: 0,
            codeInsertExecutionRefund: 0,
            calldataFloorGas: 0,
            stateGasUsed: long.MaxValue,
            refundQuotient: 2,
            isError: false,
            shouldRevert: false,
            isEip8037Enabled: true,
            isEip7778Enabled: true);

        AssertSettlement(
            result,
            spentGas: 1UL << 63,
            operationGas: 1UL << 63,
            blockGas: 1UL << 63,
            blockStateGas: long.MaxValue,
            maxUsedGas: ulong.MaxValue,
            gasRefund: long.MaxValue);
    }

    [TestCase(ulong.MaxValue, 0L, 0UL, TestName = "unsigned code refund casts to minus one and addition wraps")]
    [TestCase(0UL, long.MinValue, 1UL << 63, TestName = "minimum signed refund negation retains its bit pattern")]
    public void Signed_refund_machine_boundaries_are_unchecked(
        ulong codeInsertExecutionRefund,
        long refundCounter,
        ulong expectedOperationGas)
    {
        TransactionSettlementResult result = TransactionSettlementKernel.Calculate(
            transactionGasLimit: ulong.MaxValue,
            preRefundGas: codeInsertExecutionRefund == ulong.MaxValue ? ulong.MaxValue : 0,
            refundCounter,
            destroyCount: 0,
            destroyRefund: 0,
            codeInsertExecutionRefund,
            calldataFloorGas: 0,
            stateGasUsed: 0,
            refundQuotient: 5,
            isError: false,
            shouldRevert: false,
            isEip8037Enabled: true,
            isEip7778Enabled: true);

        Assert.That(result.OperationGas, Is.EqualTo(expectedOperationGas));
        Assert.That(result.GasRefund, Is.Zero);
    }

    private static void AssertSettlement(
        TransactionSettlementResult actual,
        ulong spentGas,
        ulong operationGas,
        ulong blockGas,
        ulong blockStateGas,
        ulong maxUsedGas,
        ulong gasRefund)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.SpentGas, Is.EqualTo(spentGas), "spent gas");
            Assert.That(actual.OperationGas, Is.EqualTo(operationGas), "operation gas");
            Assert.That(actual.BlockGas, Is.EqualTo(blockGas), "block execution gas");
            Assert.That(actual.BlockStateGas, Is.EqualTo(blockStateGas), "block state gas");
            Assert.That(actual.MaxUsedGas, Is.EqualTo(maxUsedGas), "max-used gas");
            Assert.That(actual.GasRefund, Is.EqualTo(gasRefund), "positive gas refund");
        }
    }
}
