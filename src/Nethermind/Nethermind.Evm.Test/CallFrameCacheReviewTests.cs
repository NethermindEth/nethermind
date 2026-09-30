// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Adversarial checks of the per-depth call-frame cache: state that a reused frame must not inherit (refunds,
/// access-list warmth of a reverted sibling, the static flag, the continuation flag), frames unwound by an
/// exception, frames orphaned mid-transaction by an <see cref="EvmException"/>, and a seeded random differential
/// against the uncached path.
/// </summary>
[TestFixture(false)]
[TestFixture(true)]
public class CallFrameCacheReviewTests(bool amsterdam) : VirtualMachineTestsBase
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_isDisposed")]
    private static extern ref bool IsDisposed(VmState<EthereumGasPolicy> state);

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => amsterdam ? MainnetSpecProvider.AmsterdamBlockTimestamp : MainnetSpecProvider.OsakaBlockTimestamp;

    private const ulong CallGas = 200_000;
    private const ulong TxGas = 7_000_000;

    private static readonly Address Warmer = new("0x00000000000000000000000000000000000c1e01");
    private static readonly Address Clearer = new("0x00000000000000000000000000000000000c1e02");
    private static readonly Address Stopper = new("0x00000000000000000000000000000000000c1e03");
    private static readonly Address Writer = new("0x00000000000000000000000000000000000c1e04");
    private static readonly Address Valued = new("0x00000000000000000000000000000000000c1e05");
    private static readonly Address Hopper = new("0x00000000000000000000000000000000000c1e06");
    private static readonly Address Chain = new("0x00000000000000000000000000000000000c1e07");
    private static readonly Address Probe = new("0x00000000000000000000000000000000000c1e08");
    private static readonly Address Rec = new("0x00000000000000000000000000000000000c1e09");
    private static readonly Address Eoa = new("0x00000000000000000000000000000000000c1e0a");
    private static readonly Address Missing = new("0x00000000000000000000000000000000000c1e0b");
    private static readonly Address ColdX = new("0x00000000000000000000000000000000000c1e0f");
    private static readonly Address ColdY = new("0x00000000000000000000000000000000000c1e0e");
    private static readonly Address[] Dirty =
    [
        new("0x00000000000000000000000000000000000c1e10"),
        new("0x00000000000000000000000000000000000c1e11"),
        new("0x00000000000000000000000000000000000c1e12"),
        new("0x00000000000000000000000000000000000c1e13"),
        new("0x00000000000000000000000000000000000c1e14"),
    ];

    private static readonly byte[] DirtyWord = Bytes.FromHexString("0xdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef");

    [SetUp]
    public override void Setup()
    {
        base.Setup();
        Deploy(Warmer, WarmerCode());
        Deploy(Clearer, Prepare.EvmCode.SSTORE(5, [0]).Op(Instruction.STOP).Done);
        TestState.Set(new StorageCell(Clearer, 5), (UInt256)1);
        Deploy(Stopper, [(byte)Instruction.STOP]);
        Deploy(Writer, Prepare.EvmCode.SSTORE(1, [1]).Op(Instruction.STOP).Done);
        Deploy(Valued, Prepare.EvmCode
            .Op(Instruction.SELFBALANCE).PushData(0).Op(Instruction.MSTORE)
            .Op(Instruction.CALLVALUE).PushData(32).Op(Instruction.MSTORE)
            .Op(Instruction.RETURNDATASIZE).PushData(64).Op(Instruction.MSTORE)
            .RETURN(0, 96).Done);
        Deploy(Hopper, Prepare.EvmCode.CALL(CallGas / 4, Valued, 0, 0, 0, 0x300, 96).Op(Instruction.POP).RETURN(0x300, 32).Done);
        Deploy(Chain, ChainCode());
        Deploy(Probe, ProbeCode());
        Deploy(Rec, RecCode());
        byte[][] endings =
        [
            Prepare.EvmCode.REVERT(0x200, 32).Done,
            Prepare.EvmCode.INVALID().Done,
            Prepare.EvmCode.MLOAD(1 << 30).Done,
            Prepare.EvmCode.RETURN(0x200, 32).Done,
            Prepare.EvmCode.Op(Instruction.STOP).Done,
        ];
        for (int i = 0; i < Dirty.Length; i++)
        {
            Deploy(Dirty[i], [.. DirtyPrefix(), .. endings[i]]);
        }

        TestState.CreateAccount(Eoa, 1.Ether);
        TestState.Commit(SpecProvider.GenesisSpec);
    }

    /// <summary>
    /// A committed sibling's refund stays on its (now disposed) frame object; the next child that reuses the frame
    /// must start from zero or the refund is counted twice.
    /// </summary>
    [Test]
    public void Refund_of_a_committed_sibling_is_not_counted_again_by_the_frame_reusing_it()
    {
        byte[] code = Prepare.EvmCode
            .SSTORE(1, [1]).SSTORE(2, [1]) // raise gas used so the refund cap does not hide a doubled refund
            .CALL(CallGas, Clearer, 0, 0, 0, 0, 0).Op(Instruction.POP)
            .CALL(CallGas, Stopper, 0, 0, 0, 0, 0).Op(Instruction.POP)
            .CALL(CallGas, Stopper, 0, 0, 0, 0, 0).Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        string cached = Observe(code, traced: false);
        string warm = Observe(code, traced: false);
        string uncached = Uncached(() => Observe(code, traced: false));

        Assert.That(cached, Does.StartWith("status=1"));
        Assert.That(cached, Is.EqualTo(uncached));
        Assert.That(warm, Is.EqualTo(uncached));
    }

    /// <summary>
    /// EIP-2929: warmth added by a reverted call is rolled back, also when the reused frame's previous user
    /// committed (and so was marked as not restorable).
    /// </summary>
    [Test]
    public void Access_warmth_of_a_reverted_sibling_is_rolled_back_on_the_reused_frame([Values] bool traced)
    {
        byte[] code = Prepare.EvmCode
            .MSTORE(0, ((UInt256)10).ToBigEndian()).MSTORE(32, ColdY.Bytes.PadLeft(32)).MSTORE(64, UInt256.Zero.ToBigEndian())
            .CALL(CallGas, Warmer, 0, 0, 96, 0, 0).Op(Instruction.POP) // warms slot 10 and ColdY, commits
            .MSTORE(0, ((UInt256)9).ToBigEndian()).MSTORE(32, ColdX.Bytes.PadLeft(32)).MSTORE(64, UInt256.One.ToBigEndian())
            .CALL(CallGas, Warmer, 0, 0, 96, 0, 0).Op(Instruction.POP) // warms slot 9 and ColdX, reverts
            .CALL(CallGas, Warmer, 0, 0, 0, 0x100, 128).Op(Instruction.POP) // measures
            .RETURN(0x100, 128)
            .Done;

        UInt256[] first = Words(RunAndRestore(code, traced));
        UInt256[] second = Words(RunAndRestore(code, traced));
        UInt256[] uncached = Uncached(() => Words(RunAndRestore(code, traced)));

        using (Assert.EnterMultipleScope())
        {
            // slot 9 and ColdX were warmed by the reverted call only, slot 10 and ColdY by the committed one
            if (!amsterdam) Assert.That(first, Is.EqualTo(new UInt256[] { 2107, 2607, 107, 107 }));
            Assert.That(first[0], Is.GreaterThan(first[2]), "slot 9 is cold again");
            Assert.That(first[1], Is.GreaterThan(first[3]), "ColdX is cold again");
            Assert.That(second, Is.EqualTo(first));
            Assert.That(uncached, Is.EqualTo(first));
        }
    }

    [Test]
    public void Static_context_does_not_stick_to_the_reused_frame([Values] bool traced)
    {
        byte[] code = Prepare.EvmCode
            .STATICCALL(CallGas, Writer, 0, 0, 0, 0).PushData(0).Op(Instruction.MSTORE)
            .CALL(CallGas, Writer, 0, 0, 0, 0, 0).PushData(32).Op(Instruction.MSTORE)
            .STATICCALL(CallGas, Writer, 0, 0, 0, 0).PushData(64).Op(Instruction.MSTORE)
            .CALL(CallGas, Writer, 0, 0, 0, 0, 0).PushData(96).Op(Instruction.MSTORE)
            .RETURN(0, 128)
            .Done;

        Assert.That(Words(RunAndRestore(code, traced)), Is.EqualTo(new UInt256[] { 0, 1, 0, 1 }));
        Assert.That(Words(RunAndRestore(code, traced)), Is.EqualTo(new UInt256[] { 0, 1, 0, 1 }));
    }

    /// <summary>
    /// A frame that made a call ends as a continuation; the next child reusing it must be entered as a fresh
    /// frame: value transferred, return data cleared.
    /// </summary>
    [Test]
    public void Frame_left_as_a_continuation_is_entered_fresh_with_its_value_transfer([Values] bool traced)
    {
        byte[] code = Prepare.EvmCode
            .CALL(CallGas, Hopper, 0, 0, 0, 0, 0).Op(Instruction.POP)
            .CALL(CallGas, Valued, 3, 0, 0, 0x100, 96).Op(Instruction.POP)
            .CALL(CallGas, Hopper, 0, 0, 0, 0, 0).Op(Instruction.POP)
            .CALL(CallGas, Valued, 4, 0, 0, 0x160, 96).Op(Instruction.POP)
            .RETURN(0x100, 192)
            .Done;

        UInt256 initial = 1.Ether;
        UInt256[] expected = [initial + 3, 3, 0, initial + 7, 4, 0];
        Assert.That(Words(RunAndRestore(code, traced)), Is.EqualTo(expected));
        Assert.That(Words(RunAndRestore(code, traced)), Is.EqualTo(expected));
    }

    /// <summary>
    /// An exception unwinding through entered frames (a cancelled tracer) goes through the frame cleanup scope: the
    /// cached frames are disposed, not orphaned, and serve the next transaction.
    /// </summary>
    [Test]
    public void Frames_unwound_by_a_cancellation_are_disposed_and_reused([Values("action", "instruction")] string where)
    {
        byte[] code = ChainDriver(5);
        Assert.That(RunAndRestore(code, traced: false), Is.Not.Null);
        VmState<EthereumGasPolicy>[] before = Enumerable.Range(1, 5).Select(d => Machine.FrameCache[d]!).ToArray();

        ThrowingTracer throwing = new(Machine)
        {
            ThrowAtActionDepth = where == "action" ? 3 : -1,
            ThrowAtOpcodeDepth = where == "instruction" ? 3 : -1,
        };
        Assert.Throws<OperationCanceledException>(() => RunAndRestore(code, throwing));

        using (Assert.EnterMultipleScope())
        {
            for (int depth = 1; depth <= 5; depth++)
            {
                Assert.That(Machine.FrameCache[depth], Is.SameAs(before[depth - 1]), $"depth {depth} kept");
                Assert.That(IsDisposed(Machine.FrameCache[depth]!), Is.True, $"depth {depth} disposed by the unwind");
                Assert.That(Machine.EnvironmentCache[depth]!.ExecutingAccount, Is.Null, $"env at depth {depth} released");
            }
        }

        ThrowingTracer normal = new(Machine);
        byte[]? output = RunAndRestore(code, normal);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(normal.Error, Is.Null);
            Assert.That(output, Is.EqualTo(Uncached(() => RunAndRestore(code, traced: false))));
            for (int depth = 1; depth <= 5; depth++)
            {
                Assert.That(normal.Frames.Where(f => f.Env.CallDepth == depth).Select(static f => f.Frame), Is.All.SameAs(before[depth - 1]));
            }
        }
    }

    /// <summary>
    /// An <see cref="EvmException"/> thrown after CALL staged a child and before it was entered halts the parent
    /// but not the transaction: the orphan must be replaced when its depth is reached again in the same transaction.
    /// </summary>
    [Test]
    public void Frame_orphaned_by_an_evm_exception_is_replaced_within_the_same_transaction()
    {
        byte[] code = Prepare.EvmCode
            .MSTORE(0, ((UInt256)3).ToBigEndian())
            .CALL(CallGas * 5, Chain, 0, 0, 32, 0, 0).PushData(0x100).Op(Instruction.MSTORE)
            .CALL(CallGas * 5, Chain, 0, 0, 32, 0, 0).PushData(0x120).Op(Instruction.MSTORE)
            .CALL(CallGas * 5, Chain, 0, 0, 32, 0, 0).PushData(0x140).Op(Instruction.MSTORE)
            .RETURN(0x100, 96)
            .Done;

        ThrowingTracer throwing = new(Machine) { ThrowEvmExceptionWhenStagedAtDepth = 2 };
        byte[]? output = RunAndRestore(code, throwing);
        VmState<EthereumGasPolicy> orphan = throwing.Orphan!;

        ThrowingTracer uncachedTracer = new(Machine) { ThrowEvmExceptionWhenStagedAtDepth = 2 };
        byte[]? uncached = Uncached(() => RunAndRestore(code, uncachedTracer));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(throwing.Error, Is.Null);
            Assert.That(Words(output), Is.EqualTo(new UInt256[] { 0, 1, 1 }), "the first call halted, the others succeeded");
            Assert.That(output, Is.EqualTo(uncached));
            Assert.That(orphan, Is.Not.Null);
            Assert.That(IsDisposed(orphan), Is.False, "the orphan is left alone");
            Assert.That(Machine.FrameCache[2], Is.Not.SameAs(orphan), "and replaced");
            Assert.That(throwing.Frames.Where(static f => f.Env.CallDepth == 2).Select(static f => f.Frame), Has.None.SameAs(orphan));
        }

        ThrowingTracer after = new(Machine);
        Assert.That(RunAndRestore(code, after), Is.EqualTo(Uncached(() => RunAndRestore(code, traced: false))));
        Assert.That(after.Error, Is.Null);
    }

    /// <summary>
    /// The EIP-8037 per-frame bookkeeping (create state charge, pre-existing target, the state-gas floor) belongs to
    /// the frame's own entry: creates on fresh and on pre-funded (alive, codeless) targets that succeed, revert or fail
    /// their code deposit, interleaved with slot toggles that charge and refund state gas, on the same reused frames.
    /// </summary>
    [Test]
    public void Create_and_state_gas_bookkeeping_does_not_stick_to_the_reused_frame([Values] bool traced)
    {
        byte[] ok = Prepare.EvmCode.RETURN(0, 1).Done;
        byte[] reverting = Prepare.EvmCode.REVERT(0, 0).Done;
        byte[] badCode = Prepare.EvmCode.PushData(0xEF).PushData(0).Op(Instruction.MSTORE8).RETURN(0, 1).Done;
        byte[] toggler = Prepare.EvmCode.PushData(0).Op(Instruction.SLOAD).Op(Instruction.ISZERO).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done;
        Address toggle = new("0x00000000000000000000000000000000000c1e20");
        Address toggleThenRevert = new("0x00000000000000000000000000000000000c1e21");
        Address callToggleThenRevert = new("0x00000000000000000000000000000000000c1e22");
        Address creator = new("0x00000000000000000000000000000000000c1e23");
        Deploy(toggle, toggler);
        Deploy(toggleThenRevert, [.. toggler[..^1], .. Prepare.EvmCode.REVERT(0, 0).Done]);
        Deploy(callToggleThenRevert, Prepare.EvmCode.CALL(CallGas, toggle, 0, 0, 0, 0, 0).Op(Instruction.POP).REVERT(0, 0).Done);
        // A failed code deposit burns all the gas the create frame got, so the creates run in a gas-limited creator.
        Deploy(creator, CreatorCode());

        List<(Instruction Kind, byte[] Init, int Salt)> creates =
        [
            (Instruction.CREATE, ok, 0),
            (Instruction.CREATE2, reverting, 1), // pre-funded target: not charged, pre-existing
            (Instruction.CREATE, badCode, 0), // fresh target, failed deposit
            (Instruction.CREATE2, badCode, 2), // pre-funded target, failed deposit
            (Instruction.CREATE, reverting, 0),
            (Instruction.CREATE2, ok, 3), // pre-funded target, succeeds
            (Instruction.CREATE, ok, 0),
            (Instruction.CREATE2, reverting, 4), // fresh target
        ];
        foreach ((Instruction kind, byte[] init, int salt) in creates)
        {
            if (kind == Instruction.CREATE2 && salt != 4) TestState.CreateAccount(ContractAddress.From(creator, ((UInt256)salt).ToBigEndian(), init), (UInt256)1);
        }

        TestState.Commit(SpecProvider.GenesisSpec);

        Prepare code = Prepare.EvmCode.SSTORE(9, [1]); // the driver has used state gas before its first child
        int slot = 0x1000;
        void Record()
        {
            code.PushData(slot).Op(Instruction.MSTORE)
                .Op(Instruction.RETURNDATASIZE).Op(Instruction.PUSH0).PushData(slot + 32).Op(Instruction.RETURNDATACOPY)
                .Op(Instruction.GAS).PushData(slot + 64).Op(Instruction.MSTORE);
            slot += 96;
        }

        foreach ((Instruction kind, byte[] init, int salt) in creates)
        {
            code.MSTORE(0, ((UInt256)(kind == Instruction.CREATE2 ? 1 : 0)).ToBigEndian()).MSTORE(32, ((UInt256)salt).ToBigEndian())
                .StoreDataInMemory(64, init)
                .CALL(CallGas, creator, 0, 0, (UInt256)(64 + init.Length), 0, 0);
            Record();
            code.CALL(CallGas, toggle, 0, 0, 0, 0, 0);
            Record();
            code.CALL(CallGas, kind == Instruction.CREATE ? toggleThenRevert : callToggleThenRevert, 0, 0, 0, 0, 0);
            Record();
        }

        // What exists at every CREATE2 target after the transaction.
        foreach ((Instruction kind, byte[] init, int salt) in creates)
        {
            if (kind == Instruction.CREATE2)
            {
                code.PushData(ContractAddress.From(creator, ((UInt256)salt).ToBigEndian(), init)).Op(Instruction.EXTCODEHASH);
                Record();
            }
        }

        byte[] program = code.RETURN(0x1000, (UInt256)(slot - 0x1000)).Done;
        (Transaction transaction, BlockExecutionContext context) = PrepareOnce(program);
        string cached = Observe(transaction, context, traced);
        string warm = Observe(transaction, context, traced);
        string uncached = Uncached(() => Observe(transaction, context, traced));
        UInt256[] words = Words(RunAndRestore(program, traced: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cached, Does.StartWith("status=1"));
            Assert.That(cached, Is.EqualTo(uncached));
            Assert.That(warm, Is.EqualTo(uncached));
            // The sixth create (CREATE2, salt 3) landed on its pre-funded target.
            Assert.That(words[5 * 9 + 1], Is.EqualTo(new UInt256(ContractAddress.From(creator, ((UInt256)3).ToBigEndian(), ok).Bytes.PadLeft(32), isBigEndian: true)));
        }
    }

    /// <summary>
    /// EIP-8037: a child that clears a slot a sibling created is refunded more state gas than it used itself, so the
    /// refund is advanced against its entry floor. The floor must be the reused frame's own entry, whether the child
    /// stops, reverts or halts, and whether it clears the slot itself (DELEGATECALL) or through a grandchild.
    /// </summary>
    [Test]
    public void State_gas_floor_of_a_reused_frame_is_its_own_entry([Values("stop", "revert", "invalid")] string ending, [Values] bool viaGrandchild)
    {
        byte[] toggler = Prepare.EvmCode.PushData(0).Op(Instruction.SLOAD).Op(Instruction.ISZERO).PushData(0).Op(Instruction.SSTORE).Done;
        byte[] end = ending switch
        {
            "stop" => [(byte)Instruction.STOP],
            "revert" => Prepare.EvmCode.REVERT(0, 0).Done,
            _ => Prepare.EvmCode.INVALID().Done,
        };
        Address toggle = new("0x00000000000000000000000000000000000c1e30");
        Address clearer = new("0x00000000000000000000000000000000000c1e31");
        Deploy(toggle, [.. toggler, (byte)Instruction.STOP]);
        Deploy(clearer, viaGrandchild
            ? [.. Prepare.EvmCode.CALL(CallGas, toggle, 0, 0, 0, 0, 0).Op(Instruction.POP).Done, .. end]
            : [.. toggler, .. end]);
        TestState.Commit(SpecProvider.GenesisSpec);

        Prepare code = Prepare.EvmCode.SSTORE(9, [1]);
        // The sibling creates the slot the clearer then clears: toggle's own slot through a grandchild, or the driver's.
        code = viaGrandchild ? code.CALL(CallGas, toggle, 0, 0, 0, 0, 0) : code.DELEGATECODE(CallGas, toggle, 0, 0, 0, 0);
        code.PushData(0x100).Op(Instruction.MSTORE).Op(Instruction.GAS).PushData(0x120).Op(Instruction.MSTORE);
        code = viaGrandchild ? code.CALL(CallGas, clearer, 0, 0, 0, 0, 0) : code.DELEGATECODE(CallGas, clearer, 0, 0, 0, 0);
        code.PushData(0x140).Op(Instruction.MSTORE).Op(Instruction.GAS).PushData(0x160).Op(Instruction.MSTORE)
            .CALL(CallGas, Stopper, 0, 0, 0, 0, 0).PushData(0x180).Op(Instruction.MSTORE).Op(Instruction.GAS).PushData(0x1a0).Op(Instruction.MSTORE)
            .RETURN(0x100, 0xc0);

        (Transaction transaction, BlockExecutionContext context) = PrepareOnce(code.Done);
        string cached = Observe(transaction, context, traced: false);
        string warm = Observe(transaction, context, traced: false);
        string uncached = Uncached(() => Observe(transaction, context, traced: false));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cached, Does.StartWith("status=1"));
            Assert.That(cached, Is.EqualTo(uncached));
            Assert.That(warm, Is.EqualTo(uncached));
        }
    }

    /// <summary>
    /// A frame holding an advanced state-gas refund when a cancellation unwinds it must not hand that refund to the
    /// next transaction's child reusing the frame.
    /// </summary>
    [Test]
    public void Advanced_refund_of_a_frame_unwound_by_a_cancellation_does_not_reach_its_next_user()
    {
        Address toggle = new("0x00000000000000000000000000000000000c1e32");
        Deploy(toggle, Prepare.EvmCode.PushData(0).Op(Instruction.SLOAD).Op(Instruction.ISZERO).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        TestState.Commit(SpecProvider.GenesisSpec);

        // The second toggle clears the slot the first created and is cancelled on its STOP, refund still advanced.
        byte[] cancelled = Prepare.EvmCode.SSTORE(9, [1])
            .CALL(CallGas, toggle, 0, 0, 0, 0, 0).Op(Instruction.POP)
            .CALL(CallGas, toggle, 0, 0, 0, 0, 0).Op(Instruction.POP)
            .Op(Instruction.STOP).Done;
        byte[] next = Prepare.EvmCode.SSTORE(9, [1])
            .CALL(CallGas, Stopper, 0, 0, 0, 0, 0).PushData(0).Op(Instruction.MSTORE)
            .Op(Instruction.GAS).PushData(32).Op(Instruction.MSTORE)
            .RETURN(0, 64).Done;

        ThrowingTracer cancelling = new(Machine) { ThrowAtOpcodeDepth = 1, ThrowAtOpcode = Instruction.STOP, ThrowAtOpcodeOccurrence = 2 };
        Assert.Throws<OperationCanceledException>(() => RunAndRestore(cancelled, cancelling));
        Assert.That(Machine.FrameCache[1]!.StateGasRefundAdvanced, Is.Zero, "the unwound frame keeps no advanced refund");
        // A throwing call is not restored: drop what the cancelled transaction left in the world state.
        TestState.Reset();

        (Transaction nextTransaction, BlockExecutionContext nextContext) = PrepareOnce(next);
        string expected = Uncached(() => Observe(nextTransaction, nextContext, traced: false));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Observe(nextTransaction, nextContext, traced: false), Is.EqualTo(expected));
            Assert.That(IsDisposed(Machine.FrameCache[1]!), Is.True);
        }
    }

    private static IEnumerable<TestCaseData> Seeds()
    {
        for (int seed = 1; seed <= 48; seed++)
        {
            yield return new TestCaseData(seed, seed % 6 == 0).SetArgDisplayNames(seed.ToString(), seed % 6 == 0 ? "traced" : "untraced");
        }
    }

    /// <summary>
    /// Random call trees - every call kind, precompiles, CREATE/CREATE2, recursion past the cached depth, low gas
    /// that runs out at random depths, and orphaning exceptions at random staged frames - observed exactly as on
    /// the uncached path, with the cache warmed by the previous seeds.
    /// </summary>
    [TestCaseSource(nameof(Seeds))]
    public void Random_call_trees_match_the_uncached_path(int seed, bool traced)
    {
        Random random = new(seed);
        byte[] code = RandomProgram(random);
        int orphanAt = random.Next(4) == 0 ? random.Next(1, 12) : -1;

        // Warm the cache with an unrelated program first so a leak from it would show.
        Observe(RandomProgram(new Random(seed + 1000)), traced: false);

        // Prepared once: preparing credits the driver, whose balance the probes read under CALLCODE and DELEGATECALL.
        (Transaction transaction, BlockExecutionContext context) = PrepareOnce(code);
        string cached = Observe(transaction, context, traced, orphanAt, memory: false);
        string warm = Observe(transaction, context, traced, orphanAt, memory: false);
        string uncached = Uncached(() => Observe(transaction, context, traced, orphanAt, memory: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cached, Is.EqualTo(uncached));
            Assert.That(warm, Is.EqualTo(uncached));
        }
    }

    private byte[] RandomProgram(Random random)
    {
        const int logStart = 0x1000;
        const int entrySize = 64 + 352;
        Prepare code = Prepare.EvmCode.MSTORE(0, DirtyWord).MSTORE(32, DirtyWord);
        int steps = random.Next(6, 18);
        Address[] targets = [Probe, Rec, Rec, Chain, Eoa, Missing, Writer, Hopper, Clearer,
            new("0x0000000000000000000000000000000000000001"), new("0x0000000000000000000000000000000000000002"),
            new("0x0000000000000000000000000000000000000004"), .. Dirty];
        ulong[] gases = [3_000, 30_000, 200_000, 1_000_000, 3_000_000];
        for (int step = 0; step < steps; step++)
        {
            int entry = logStart + step * entrySize;
            int roll = random.Next(10);
            if (roll == 0)
            {
                byte[] init = random.Next(2) == 0
                    ? Prepare.EvmCode.CALL(CallGas, Probe, 0, 0, 0, 0, 0).Op(Instruction.POP).CALL(CallGas, Dirty[0], 0, 0, 0, 0, 0).Op(Instruction.POP).RETURN(0, 1).Done
                    : [.. DirtyPrefix(), .. Prepare.EvmCode.CALL(CallGas, Rec, 0, 0, 0, 0, 0).Op(Instruction.POP).REVERT(0x200, 32).Done];
                code.StoreDataInMemory(0x800, init);
                bool create2 = random.Next(2) == 0;
                if (create2) code.PushData(step);
                code.PushData(init.Length).PushData(0x800).PushData(random.Next(2)).Op(create2 ? Instruction.CREATE2 : Instruction.CREATE);
            }
            else
            {
                int kind = random.Next(4);
                Address target = targets[random.Next(targets.Length)];
                // Rec and Chain call ADDRESS, which under CALLCODE or DELEGATECALL would re-enter this driver.
                if (kind is 1 or 2 && (target == Rec || target == Chain)) kind = 0;
                int inLength = 36;
                if (target == Rec || target == Chain)
                {
                    code.MSTORE(0, ((UInt256)random.Next(0, 14)).ToBigEndian()).MSTORE(32, ((UInt256)random.Next(0, 6)).ToBigEndian());
                    inLength = 64;
                }

                UInt256 gas = gases[random.Next(gases.Length)];
                (UInt256 outOffset, UInt256 outLength) = random.Next(3) == 0 ? ((UInt256)0x40, (UInt256)64) : (UInt256.Zero, UInt256.Zero);
                UInt256 value = (UInt256)random.Next(3);
                UInt256 inLen = (UInt256)inLength;
                _ = kind switch
                {
                    0 => code.CALL(gas, target, value, 0, inLen, outOffset, outLength),
                    1 => code.CALLCODE(gas, target, value, 0, inLen, outOffset, outLength),
                    2 => code.DELEGATECODE(gas, target, 0, inLen, outOffset, outLength),
                    _ => code.STATICCALL(gas, target, 0, inLen, outOffset, outLength),
                };
                if (random.Next(3) == 0) code.MSTORE(0, DirtyWord);
            }

            code.PushData(entry).Op(Instruction.MSTORE)
                .Op(Instruction.RETURNDATASIZE).PushData(entry + 32).Op(Instruction.MSTORE)
                .Op(Instruction.RETURNDATASIZE).Op(Instruction.PUSH0).PushData(entry + 64).Op(Instruction.RETURNDATACOPY);
        }

        return code.RETURN(logStart, (UInt256)(steps * entrySize)).Done;
    }

    private byte[] ChainDriver(int n) => Prepare.EvmCode
        .MSTORE(0, ((UInt256)n).ToBigEndian())
        .CALL(CallGas * 10, Chain, 0, 0, 32, 0, 32).Op(Instruction.POP)
        .RETURN(0, 32)
        .Done;

    private (Transaction Transaction, BlockExecutionContext Context) PrepareOnce(byte[] code)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, TxGas, code, value: 0);
        return (transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)));
    }

    private string Observe(byte[] code, bool traced, int orphanAt = -1, bool memory = true)
    {
        (Transaction transaction, BlockExecutionContext context) = PrepareOnce(code);
        return Observe(transaction, context, traced, orphanAt, memory);
    }

    private string Observe(Transaction transaction, in BlockExecutionContext context, bool traced, int orphanAt = -1, bool memory = true)
    {
        StringBuilder text = new();
        if (traced && orphanAt < 0)
        {
            GethLikeTxMemoryTracer tracer = new(transaction, GethTraceOptions.Default with { EnableMemory = memory });
            _processor.CallAndRestore(transaction, context, tracer);
            GethLikeTxTrace trace = tracer.BuildResult();
            text.Append($"status={(trace.Failed ? 0 : 1)} gas={trace.Gas} out={Convert.ToHexString(trace.ReturnValue)}\n");
            foreach (GethTxTraceEntry entry in trace.Entries)
            {
                text.Append($"{entry.Depth} {entry.ProgramCounter} {entry.Opcode} {entry.Gas} {entry.GasCost} {entry.Error} ");
                text.Append(entry.Stack is { } stack ? Convert.ToHexString(stack.Span) : "-").Append(' ');
                text.Append(entry.Memory is { } mem ? Convert.ToHexString(mem.Span) : "-").Append('\n');
            }

            return text.ToString();
        }

        ThrowingTracer observer = new(Machine) { ThrowEvmExceptionAtStagedCount = orphanAt, Traced = traced };
        _processor.CallAndRestore(transaction, context, observer);
        text.Append($"status={observer.StatusCode} gas={observer.GasSpent} error={observer.Error} out={Convert.ToHexString(observer.ReturnValue ?? [])}\n");
        foreach (LogEntry log in observer.Logs)
        {
            text.Append($"log {log.Address} {string.Join(",", log.Topics.Select(static t => t.ToString()))} {Convert.ToHexString(log.Data)}\n");
        }

        return text.ToString();
    }

    private byte[]? RunAndRestore(byte[] code, bool traced) => RunAndRestore(code, new ThrowingTracer(Machine) { Traced = traced });

    private byte[]? RunAndRestore(byte[] code, ThrowingTracer tracer)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, TxGas, code, value: 0);
        _processor.CallAndRestore(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        return tracer.ReturnValue;
    }

    private T Uncached<T>(Func<T> run)
    {
        FieldInfo frames = typeof(VirtualMachine<EthereumGasPolicy>).GetField(nameof(VirtualMachine<EthereumGasPolicy>.FrameCache), BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo envs = typeof(VirtualMachine<EthereumGasPolicy>).GetField(nameof(VirtualMachine<EthereumGasPolicy>.EnvironmentCache), BindingFlags.Instance | BindingFlags.NonPublic)!;
        object cachedFrames = frames.GetValue(Machine)!;
        object cachedEnvs = envs.GetValue(Machine)!;
        try
        {
            frames.SetValue(Machine, Array.Empty<VmState<EthereumGasPolicy>?>());
            envs.SetValue(Machine, Array.Empty<ExecutionEnvironment?>());
            return run();
        }
        finally
        {
            frames.SetValue(Machine, cachedFrames);
            envs.SetValue(Machine, cachedEnvs);
        }
    }

    private void Deploy(Address address, byte[] code)
    {
        TestState.CreateAccount(address, 1.Ether);
        TestState.InsertCode(address, code, SpecProvider.GenesisSpec);
    }

    private static UInt256[] Words(byte[]? data)
    {
        Assert.That(data, Is.Not.Null);
        UInt256[] words = new UInt256[data!.Length / 32];
        for (int i = 0; i < words.Length; i++) words[i] = new UInt256(data.AsSpan(i * 32, 32), isBigEndian: true);
        return words;
    }

    /// <summary>Warms slot word0 and account word1 then reverts when word2 is set; with no input measures warmth.</summary>
    private static byte[] WarmerCode()
    {
        Asm a = new();
        a.Op(Instruction.CALLDATASIZE, Instruction.ISZERO); int toMeasure = a.Jump(Instruction.JUMPI);
        a.Op(Instruction.PUSH0, Instruction.CALLDATALOAD, Instruction.SLOAD, Instruction.POP);
        a.Push1(32); a.Op(Instruction.CALLDATALOAD, Instruction.BALANCE, Instruction.POP);
        a.Push1(64); a.Op(Instruction.CALLDATALOAD); int toRevert = a.Jump(Instruction.JUMPI);
        a.Op(Instruction.STOP);
        a.Label(toRevert); a.Op(Instruction.PUSH0, Instruction.PUSH0, Instruction.REVERT);
        a.Label(toMeasure);
        a.Measure(static a => { a.Push1(9); a.Op(Instruction.SLOAD); }, 0);
        a.Measure(static a => a.Push(ColdX), 32, Instruction.BALANCE);
        a.Measure(static a => { a.Push1(10); a.Op(Instruction.SLOAD); }, 64);
        a.Measure(static a => a.Push(ColdY), 96, Instruction.BALANCE);
        a.Push1(128); a.Op(Instruction.PUSH0, Instruction.RETURN);
        return a.Done();
    }

    /// <summary>Input (kind, salt, init code): CREATE (kind 0) or CREATE2 of the init code; returns the result.</summary>
    private static byte[] CreatorCode()
    {
        Asm a = new();
        a.Push1(64); a.Op(Instruction.CALLDATASIZE, Instruction.SUB); // [len]
        a.Op(Instruction.DUP1); a.Push1(64); a.Op(Instruction.PUSH0, Instruction.CALLDATACOPY); // mem[0..len) = init
        a.Op(Instruction.PUSH0, Instruction.CALLDATALOAD); int toCreate2 = a.Jump(Instruction.JUMPI);
        a.Op(Instruction.PUSH0, Instruction.PUSH0, Instruction.CREATE, Instruction.PUSH0, Instruction.MSTORE);
        a.Push1(32); a.Op(Instruction.PUSH0, Instruction.RETURN);
        a.Label(toCreate2);
        a.Push1(32); a.Op(Instruction.CALLDATALOAD, Instruction.SWAP1, Instruction.PUSH0, Instruction.PUSH0, Instruction.CREATE2, Instruction.PUSH0, Instruction.MSTORE);
        a.Push1(32); a.Op(Instruction.PUSH0, Instruction.RETURN);
        return a.Done();
    }

    /// <summary>Calls itself with n - 1 while n > 0, then stops.</summary>
    private static byte[] ChainCode()
    {
        Asm a = new();
        a.Op(Instruction.PUSH0, Instruction.CALLDATALOAD, Instruction.DUP1, Instruction.ISZERO); int toEnd = a.Jump(Instruction.JUMPI);
        a.Push1(1); a.Op(Instruction.SWAP1, Instruction.SUB, Instruction.PUSH0, Instruction.MSTORE);
        a.Op(Instruction.PUSH0, Instruction.PUSH0); a.Push1(32); a.Op(Instruction.PUSH0, Instruction.PUSH0, Instruction.ADDRESS, Instruction.GAS, Instruction.CALL);
        a.Push1(0x20); a.Op(Instruction.MSTORE); a.Push1(32); a.Push1(0x20); a.Op(Instruction.RETURN);
        a.Label(toEnd); a.Op(Instruction.STOP);
        return a.Done();
    }

    /// <summary>
    /// Input (n, r): writes storage, memory and a log, calls itself with (n - 1, r), returns
    /// child + 1 + 1000 * childSucceeded, reverting with it when n % r == 0.
    /// </summary>
    private static byte[] RecCode()
    {
        Asm a = new();
        a.Op(Instruction.PUSH0, Instruction.CALLDATALOAD, Instruction.DUP1, Instruction.ISZERO); int toZero = a.Jump(Instruction.JUMPI);
        a.Push1(1); a.Op(Instruction.DUP2, Instruction.SUB, Instruction.PUSH0, Instruction.MSTORE);
        a.Push1(32); a.Op(Instruction.CALLDATALOAD); a.Push1(32); a.Op(Instruction.MSTORE);
        a.Op(Instruction.DUP1, Instruction.DUP1, Instruction.SSTORE);
        a.Op(Instruction.DUP1, Instruction.DUP1, Instruction.TSTORE);
        a.Op(Instruction.DUP1); a.Push2(0x300); a.Op(Instruction.MSTORE); a.Push1(32); a.Push2(0x300); a.Op(Instruction.LOG0);
        a.Push1(32); a.Push1(0x40); a.Push1(64); a.Op(Instruction.PUSH0, Instruction.PUSH0, Instruction.ADDRESS, Instruction.GAS, Instruction.CALL);
        a.Push2(1000); a.Op(Instruction.MUL); a.Push1(0x40); a.Op(Instruction.MLOAD, Instruction.ADD); a.Push1(1); a.Op(Instruction.ADD);
        a.Push1(0x80); a.Op(Instruction.MSTORE);
        a.Push1(32); a.Op(Instruction.CALLDATALOAD, Instruction.SWAP1, Instruction.MOD); int toReturn = a.Jump(Instruction.JUMPI);
        a.Push1(32); a.Push1(0x80); a.Op(Instruction.REVERT);
        a.Label(toReturn); a.Push1(32); a.Push1(0x80); a.Op(Instruction.RETURN);
        a.Label(toZero); a.Op(Instruction.PUSH0, Instruction.PUSH0, Instruction.MSTORE); a.Push1(32); a.Op(Instruction.PUSH0, Instruction.RETURN);
        return a.Done();
    }

    /// <summary>Returns what a fresh frame must observe on entry.</summary>
    private static byte[] ProbeCode()
    {
        Prepare probe = Prepare.EvmCode
            .Op(Instruction.MSIZE)
            .Op(Instruction.RETURNDATASIZE)
            .Op(Instruction.CALLDATASIZE)
            .Op(Instruction.CALLVALUE)
            .Op(Instruction.CALLER)
            .Op(Instruction.ADDRESS)
            .Op(Instruction.SELFBALANCE)
            .PushData(0).Op(Instruction.CALLDATALOAD)
            .MLOAD(0x200)
            .SLOAD(7)
            .TLOAD(7);
        for (int word = 10; word >= 0; word--)
        {
            probe.PushData(word * 32).Op(Instruction.MSTORE);
        }

        return probe.RETURN(0, 11 * 32).Done;
    }

    private static byte[] DirtyPrefix() => Prepare.EvmCode
        .CALL(CallGas / 4, Probe, 0, 0, 0, 0x300, 32).Op(Instruction.POP)
        .MSTORE(0x200, DirtyWord)
        .SSTORE(7, [0x55])
        .TSTORE(7, [0x66])
        .LOGx(0, 0x200, 32)
        .PushData(1).PushData(2).PushData(3).PushData(4).PushData(5)
        .Done;

    private sealed class Asm
    {
        private readonly List<byte> _code = [];

        public void Op(params Instruction[] ops) => _code.AddRange(ops.Select(static o => (byte)o));
        public void Push1(byte value) { Op(Instruction.PUSH1); _code.Add(value); }
        public void Push2(int value) { Op(Instruction.PUSH2); _code.Add((byte)(value >> 8)); _code.Add((byte)value); }
        public void Push(Address address) { Op(Instruction.PUSH20); _code.AddRange(address.Bytes); }

        /// <summary>Emits a PUSH2 placeholder and <paramref name="jump"/>; returns the placeholder's position.</summary>
        public int Jump(Instruction jump)
        {
            int at = _code.Count;
            Push2(0);
            Op(jump);
            return at;
        }

        public void Label(int placeholder)
        {
            int destination = _code.Count;
            Op(Instruction.JUMPDEST);
            _code[placeholder + 1] = (byte)(destination >> 8);
            _code[placeholder + 2] = (byte)destination;
        }

        /// <summary>Stores at <paramref name="offset"/> the gas of: body, optional op, POP, GAS.</summary>
        public void Measure(Action<Asm> body, byte offset, Instruction? then = null)
        {
            Op(Instruction.GAS);
            body(this);
            if (then is { } op) Op(op);
            Op(Instruction.POP, Instruction.GAS, Instruction.SWAP1, Instruction.SUB);
            Push1(offset);
            Op(Instruction.MSTORE);
        }

        public byte[] Done() => [.. _code];
    }

    private sealed class ThrowingTracer(EthereumVirtualMachine machine) : TxTracer
    {
        public override bool IsTracingReceipt => true;
        public override bool IsTracingActions => true;
        public override bool IsTracingInstructions => Traced || ThrowAtOpcodeDepth >= 0;

        public bool Traced { get; init; }
        public int ThrowAtActionDepth { get; init; } = -1;
        public int ThrowAtOpcodeDepth { get; init; } = -1;
        public Instruction ThrowAtOpcode { get; init; } = Instruction.CALL;
        public int ThrowAtOpcodeOccurrence { get; init; } = 1;
        private int _opcodeHits;
        public int ThrowEvmExceptionWhenStagedAtDepth { get; set; } = -1;
        public int ThrowEvmExceptionAtStagedCount { get; init; } = -1;
        private int _staged;

        public VmState<EthereumGasPolicy>? Orphan { get; private set; }
        public List<(VmState<EthereumGasPolicy> Frame, ExecutionEnvironment Env)> Frames { get; } = [];
        public byte[]? ReturnValue { get; private set; }
        public ulong GasSpent { get; private set; }
        public string? Error { get; private set; }
        public byte StatusCode { get; private set; }
        public LogEntry[] Logs { get; private set; } = [];

        public override void ReportAction(ulong gas, UInt256 value, Address from, Address to,
            ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
        {
            if (callType == ExecutionType.TRANSACTION) return;
            VmState<EthereumGasPolicy> frame = machine.VmState;
            Frames.Add((frame, frame.Env));
            if (frame.Env.CallDepth == ThrowAtActionDepth) throw new OperationCanceledException("cancelled in an entered frame");
        }

        public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
        {
            if (env.CallDepth == ThrowAtOpcodeDepth && opcode == ThrowAtOpcode && ++_opcodeHits == ThrowAtOpcodeOccurrence)
            {
                throw new OperationCanceledException("cancelled mid-frame");
            }
        }

        public override void ReportActionRemainingGas(ulong gas)
        {
            if (machine.ReturnData is not VmState<EthereumGasPolicy> staged || IsDisposed(staged) || !ReferenceEquals(staged, LastStaged())) return;

            _staged++;
            if (staged.Env.CallDepth == ThrowEvmExceptionWhenStagedAtDepth || _staged == ThrowEvmExceptionAtStagedCount)
            {
                ThrowEvmExceptionWhenStagedAtDepth = -1;
                Orphan = staged;
                throw new OutOfGasException();
            }
        }

        // A staged frame is the machine's pending ReturnData that is not the frame being executed.
        private VmState<EthereumGasPolicy>? LastStaged() =>
            machine.ReturnData is VmState<EthereumGasPolicy> staged && !ReferenceEquals(staged, machine.VmState) ? staged : null;

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            GasSpent = gasSpent.SpentGas;
            ReturnValue = output;
            Logs = logs;
            StatusCode = Evm.StatusCode.Success;
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            GasSpent = gasSpent.SpentGas;
            ReturnValue = output;
            Error = error;
            StatusCode = Evm.StatusCode.Failure;
        }
    }
}
