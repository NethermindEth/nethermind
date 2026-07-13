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
/// Differential regression tests for EIP-8037 state-gas spill-refund accounting.
/// </summary>
/// <remarks>
/// Every expected (spentGas, blockRegularGas, blockStateGas) triple was produced by executing
/// the identical scenario (same bytecode, addresses, tx parameters) through the EELS amsterdam
/// reference implementation at ethereum/execution-specs commit d0338f561 — the commit the
/// tests-glamsterdam-devnet@v6.1.1 fixtures are built from (block_output.block_gas_used /
/// block_state_gas_used / receipt gas). They pin the cross-frame refund-advance, source-based
/// LIFO refill, spill non-inheritance on revert/halt, and top-level-halt block-split semantics.
/// </remarks>
[TestFixture]
public class Eip8037EelsReferenceTests : VirtualMachineTestsBase
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
        ulong ExpectedBlockRegularGas,
        long ExpectedBlockStateGas,
        bool IsCreate = false,
        byte[]? TxData = null,
        bool WithAuthorization = false)
    {
        public override string ToString() => Name;
    }

    // [CALLVALUE, PUSH2 d, JUMPI, <main>, JUMPDEST, <clear>] — calling with value != 0 selects
    // the clear section, letting another contract re-enter this one to clear its slots.
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
            ExpectedSuccess: true, ExpectedSpentGas: 125_926, ExpectedBlockRegularGas: 28_006, ExpectedBlockStateGas: 97_920);

        // Set + clear in the same frame: the refund LIFO-refills gas_left, not the reservoir.
        yield return new Scenario(
            "set_clear_same_frame_refills_gas_left",
            1_000_000,
            Prepare.EvmCode
                .PushData(1).PushData(0).Op(Instruction.SSTORE)
                .PushData(0).PushData(0).Op(Instruction.SSTORE)
                .Op(Instruction.STOP).Done,
            [],
            ExpectedSuccess: true, ExpectedSpentGas: 22_490, ExpectedBlockRegularGas: 28_112, ExpectedBlockStateGas: 0);

        // Cross-frame refund advance fully discharged against the setter's usage; all frames
        // succeed. A sets a slot, M re-enters A (value call selects clear mode) to clear it.
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
                ExpectedSuccess: true, ExpectedSpentGas: 31_432, ExpectedBlockRegularGas: 39_289, ExpectedBlockStateGas: 0);
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
                ExpectedSuccess: false, ExpectedSpentGas: 39_295, ExpectedBlockRegularGas: 39_295, ExpectedBlockStateGas: 0);
        }

        // Spill imported from a reverted grandchild rides through the child's exceptional halt.
        // The old gross spill merge docked the parent reservoir, overcharging the sender by
        // exactly the grandchild's spill.
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
                ExpectedSuccess: true, ExpectedSpentGas: 818_023, ExpectedBlockRegularGas: 818_023, ExpectedBlockStateGas: 0);
        }

        // The double-marking trace from PR #12369 review: M imports spill from reverted X and
        // receives a same-frame NEW_ACCOUNT refund (soft-failed value call to a dead account),
        // then returns successfully into P (own spill), and P halts.
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
                ExpectedSuccess: true, ExpectedSpentGas: 1_218_023, ExpectedBlockRegularGas: 1_218_023, ExpectedBlockStateGas: 0);

            // Same shape ending in a top-level exceptional halt with non-refunded intrinsic
            // state (fresh authority): the block split must keep the full intrinsic state.
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
                ExpectedSuccess: false, ExpectedSpentGas: 3_000_000, ExpectedBlockRegularGas: 2_781_210, ExpectedBlockStateGas: 218_790,
                WithAuthorization: true);
        }

        // Child-halt spill then top-level halt: burned spill stays in the regular dimension;
        // block state gas keeps the full intrinsic state (no spill reattribution).
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
                ExpectedSuccess: false, ExpectedSpentGas: 3_000_000, ExpectedBlockRegularGas: 2_781_210, ExpectedBlockStateGas: 218_790,
                WithAuthorization: true);
        }

        // The IncorporateChildStateGasRefunds combinator case: P' (clear mode) imports spill
        // from reverted X, clears two of P's slots (advance 2*SSET), M discharges exactly one
        // SSET against its own SSTORE, and P halts with the re-advanced remainder revoked.
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
                ExpectedSuccess: true, ExpectedSpentGas: 1_518_023, ExpectedBlockRegularGas: 1_518_023, ExpectedBlockStateGas: 0);

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
                ExpectedSuccess: false, ExpectedSpentGas: 3_000_000, ExpectedBlockRegularGas: 2_781_210, ExpectedBlockStateGas: 218_790,
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
                ExpectedSuccess: true, ExpectedSpentGas: 248_141, ExpectedBlockRegularGas: 52_301, ExpectedBlockStateGas: 195_840);
        }

        // Successful inner CREATE spill followed by a top-level OOG in a create tx: the tx-level
        // create-state refund nets block state gas to zero.
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
                ExpectedSuccess: false, ExpectedSpentGas: 116_400, ExpectedBlockRegularGas: 116_400, ExpectedBlockStateGas: 0,
                IsCreate: true,
                TxData: initCode);
        }

        // CREATE whose deposit fails on invalid code (EIP-3541 0xEF prefix): the child create
        // frame halts (RevertRefundToHalt path) and the parent CREATE state gas is refunded.
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
                ExpectedSuccess: true, ExpectedSpentGas: 1_788_428, ExpectedBlockRegularGas: 1_788_428, ExpectedBlockStateGas: 0);
        }
    }

    [TestCaseSource(nameof(Scenarios))]
    public void Eip8037_block_gas_split_matches_eels_reference(Scenario scenario)
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
        // The EELS reference charges cold access costs; access tracing would pre-warm every
        // touched account/slot and shift all expected values.
        tracer.IsTracingAccess = false;
        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(scenario.ExpectedSuccess ? StatusCode.Success : StatusCode.Failure), "status");
            Assert.That(tracer.GasConsumedResult.SpentGas, Is.EqualTo(scenario.ExpectedSpentGas), "spent gas");
            Assert.That(tracer.GasConsumedResult.BlockGas, Is.EqualTo(scenario.ExpectedBlockRegularGas), "block regular gas");
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(scenario.ExpectedBlockStateGas), "block state gas");
        }
    }
}
