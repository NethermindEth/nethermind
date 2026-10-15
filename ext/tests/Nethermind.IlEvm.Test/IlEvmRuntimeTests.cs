// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.IlEvm.Test;

/// <summary>
/// The Adaptive rules' lookup: shipped and live contracts found by keccak or by content, the slot's marks per runner, the
/// runtime version, fork misses, the thread-kind bits, and the profiles a counting runner keeps.
/// </summary>
[NonParallelizable]
public class IlEvmRuntimeTests : IlEvmAdaptiveFixture
{
    [Test]
    public void Shipped_code_runs_compiled_on_every_kind_of_thread_and_other_code_is_declined_from_its_mark()
    {
        byte[] code = Unique(LoopCode(), 0x01);
        IlEvm.IlContract contract = Ship(code)[0];
        Assert.That((contract.Origin, contract.RunMask, contract.Profile?.Shipped), Is.EqualTo((IlOrigin.Shipped, IlRuntime.RunAll, contract)));
        RunCounter runs = CountRuns(contract);
        Outcome expected = Interpret(code, LoopInput);
        CodeInfo codeInfo = Stamped(code);
        long resolved = IlRuntime.StatesResolved;
        Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, LoopInput)), Is.EqualTo(expected), "block processing's thread");
        Assert.That(Run(BlockVm, codeInfo, LoopInput), Is.EqualTo(expected), "another thread of block processing's runner");
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected), "a read-only query");
        Assert.That(runs.Runs, Is.EqualTo(3));
        IlEvm.IlCodeState state = StateOf(codeInfo);
        Assert.That((state.BlockRun, state.QueryRun, state.Version), Is.EqualTo((contract, contract, IlRuntime.Version)));
        Assert.That(IlRuntime.StatesResolved - resolved, Is.EqualTo(1), "one state serves both runners");
        Assert.That(contract.AttachedAt, Is.GreaterThan(0), "the check's clock starts at the first frame");

        // Code nothing shipped is marked for both runners, which decline it from the slot alone.
        byte[] other = Unique(ArithmeticCode(), 0x02);
        CodeInfo otherInfo = Stamped(other);
        Outcome otherExpected = Interpret(other, ArithmeticInput);
        Assert.That(OnBlockThread(() => Run(BlockVm, otherInfo, ArithmeticInput)), Is.EqualTo(otherExpected));
        Assert.That(IlEvmFrameRunner.CodeSlot(otherInfo), Is.SameAs(IlRuntime.QuietAll));
        long lookups = IlRuntime.Lookups;
        Assert.That(Run(QueryVm, otherInfo, ArithmeticInput), Is.EqualTo(otherExpected));
        Assert.That(OnBlockThread(() => Run(BlockVm, otherInfo, ArithmeticInput)), Is.EqualTo(otherExpected));
        Assert.That(IlRuntime.Lookups, Is.EqualTo(lookups), "a mark declines with no lookup");
        Assert.That(IlEvm.IsCompiled(otherInfo), Is.False);
    }

    [Test]
    public void Block_threads_and_the_list_s_use_flags_decide_which_threads_run_compiled_code()
    {
        // BlockThreads=Main: the other threads of block processing's runner leave the shipped contract to the interpreter.
        IlRuntime.BlockThreadsMask = IlRuntime.RunMain;
        byte[] code = Unique(LoopCode(), 0x03);
        IlEvm.IlContract contract = Ship(code)[0];
        Assert.That(contract.RunMask, Is.EqualTo(IlRuntime.RunMain | IlRuntime.RunQuery));
        RunCounter runs = CountRuns(contract);
        CodeInfo codeInfo = Stamped(code);
        Outcome expected = Interpret(code, LoopInput);
        Assert.That(Run(BlockVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(0), "a worker declines");
        Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, LoopInput)), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(1));
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(2), "read-only queries run it, whichever block threads may");

        // An entry the list found to gain only on the workers, use=w: block processing's own thread declines.
        IlRuntime.BlockThreadsMask = IlRuntime.RunMain | IlRuntime.RunWorkers;
        byte[] workerCode = Unique(LoopCode(), 0x04);
        IlEvm.IlContract workers = Ship(new IlEvmPrecompileList.Entry(ValueKeccak.Compute(workerCode), Use: IlEvmUse.Workers, MinVisitLog2: 9), workerCode);
        Assert.That(workers.RunMask, Is.EqualTo(IlRuntime.RunWorkers | IlRuntime.RunQuery));
        Assert.That((workers.Profile!.UseMask, workers.Profile.BlockMinVisitLog2), Is.EqualTo(((byte)(IlRuntime.RunWorkers | IlRuntime.RunQuery), (byte)9)));
        RunCounter workerRuns = CountRuns(workers);
        CodeInfo workerInfo = Stamped(workerCode);
        Outcome workerExpected = Interpret(workerCode, LoopInput);
        Assert.That(OnBlockThread(() => Run(BlockVm, workerInfo, LoopInput)), Is.EqualTo(workerExpected));
        Assert.That(workerRuns.Runs, Is.EqualTo(0));
        Assert.That(Run(BlockVm, workerInfo, LoopInput), Is.EqualTo(workerExpected));
        Assert.That(workerRuns.Runs, Is.EqualTo(1));

        // A bit cleared, as a check's probation clears its runner's, holds from the next part.
        workers.RunMask &= unchecked((byte)~IlRuntime.RunWorkers);
        Assert.That(Run(BlockVm, workerInfo, LoopInput), Is.EqualTo(workerExpected));
        Assert.That(workerRuns.Runs, Is.EqualTo(1));
    }

    [Test]
    public void Quiet_sentinel_per_runner()
    {
        // Block processing's mark does not keep the read-only queries' runner from what live profiling of queries
        // promoted, which block processing does not run: the query runner's state replaces the mark.
        byte[] code = Unique(LoopCode(), 0x05);
        CodeInfo codeInfo = Stamped(code);
        IlEvm.IlContract live = Promote(code, IlOrigin.LiveRpc);
        RunCounter runs = CountRuns(live);
        Outcome expected = Interpret(code, LoopInput);
        Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, LoopInput)), Is.EqualTo(expected));
        Assert.That(IlEvmFrameRunner.CodeSlot(codeInfo), Is.SameAs(IlRuntime.QuietBlock), "block processing has nothing, the queries have");
        Assert.That(runs.Runs, Is.EqualTo(0));
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(1));
        IlEvm.IlCodeState state = StateOf(codeInfo);
        Assert.That((state.QueryRun, state.BlockRun), Is.EqualTo((live, (IlEvm.IlContract?)null)));

        // Block processing now meets the state, and declines the code still.
        Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, LoopInput)), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(1));
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(2));
    }

    [Test]
    public void Quiet_reminted_on_growth()
    {
        byte[] code = Unique(LoopCode(), 0x06);
        CodeInfo codeInfo = Stamped(code);
        Outcome expected = Interpret(code, LoopInput);
        Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, LoopInput)), Is.EqualTo(expected));
        Assert.That(IlEvmFrameRunner.CodeSlot(codeInfo), Is.SameAs(IlRuntime.QuietAll));

        // What only the queries may run grew: block processing takes its own mark over the old one, without a lookup.
        Promote(Unique(LoopCode(), 0x07), IlOrigin.LiveRpc);
        long lookups = IlRuntime.Lookups, resolved = IlRuntime.StatesResolved;
        Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, LoopInput)), Is.EqualTo(expected));
        Assert.That(IlEvmFrameRunner.CodeSlot(codeInfo), Is.SameAs(IlRuntime.QuietBlock));
        Assert.That((IlRuntime.Lookups, IlRuntime.StatesResolved), Is.EqualTo((lookups, resolved)));
        // The queries' runner, whose marks are new, looks the code up once, and marks it for both again.
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(IlRuntime.Lookups, Is.EqualTo(lookups + 1));
        Assert.That(IlEvmFrameRunner.CodeSlot(codeInfo), Is.SameAs(IlRuntime.QuietAll));

        // A promotion block processing runs: the old marks resolve once more, and attach.
        IlEvm.IlContract live = Promote(code, IlOrigin.LiveBlocks);
        RunCounter runs = CountRuns(live);
        Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, LoopInput)), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(1));
        Assert.That(IlRuntime.StatesResolved, Is.EqualTo(resolved + 1));
        IlEvm.IlCodeState state = StateOf(codeInfo);
        Assert.That((state.BlockRun, state.QueryRun), Is.EqualTo((live, live)), "a contract live profiling of blocks promoted runs on both");
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(2));
        Assert.That(IlRuntime.StatesResolved, Is.EqualTo(resolved + 1));
    }

    [Test]
    public void Version_refresh()
    {
        byte[] code = Unique(LoopCode(), 0x08);
        IlEvm.IlContract contract = Ship(code)[0];
        RunCounter runs = CountRuns(contract);
        CodeInfo codeInfo = Stamped(code);
        Outcome expected = Interpret(code, LoopInput);
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        IlEvm.IlCodeState state = StateOf(codeInfo);
        int version = state.Version;
        long refreshed = IlRuntime.StatesRefreshed;

        // Another code's promotion moves the version: the state resolves again on its next part, once, for both runners.
        Promote(Unique(LoopCode(), 0x09), IlOrigin.LiveBlocks);
        Assert.That(IlRuntime.Version, Is.GreaterThan(version));
        for (int i = 0; i < 3; i++)
        {
            Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
            Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, LoopInput)), Is.EqualTo(expected));
        }

        Assert.That(IlRuntime.StatesRefreshed - refreshed, Is.EqualTo(1));
        Assert.That(StateOf(codeInfo), Is.SameAs(state));
        Assert.That((state.Version, state.QueryRun, state.BlockRun), Is.EqualTo((IlRuntime.Version, contract, contract)));
        Assert.That(runs.Runs, Is.EqualTo(7));
    }

    [Test]
    public void A_release_resolves_each_state_once_and_returns_code_with_nothing_left_to_the_mark()
    {
        byte[] code = Unique(LoopCode(), 0x13);
        IlEvm.IlContract live = Promote(code, IlOrigin.LiveBlocks);
        RunCounter runs = CountRuns(live);
        CodeInfo codeInfo = Stamped(code);
        Outcome expected = Interpret(code, LoopInput);
        Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, LoopInput)), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(1));

        // Released, as a demotion releases it: the next part resolves the state once, finds nothing, and marks the slot.
        live.Profile!.Live = null;
        IlRuntime.Shrank();
        long refreshed = IlRuntime.StatesRefreshed;
        Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, LoopInput)), Is.EqualTo(expected));
        Assert.That(IlEvmFrameRunner.CodeSlot(codeInfo), Is.SameAs(IlRuntime.QuietAll));
        long lookups = IlRuntime.Lookups;
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, LoopInput)), Is.EqualTo(expected));
        Assert.That((IlRuntime.StatesRefreshed - refreshed, IlRuntime.Lookups), Is.EqualTo((1L, lookups)));
        Assert.That(runs.Runs, Is.EqualTo(1));
    }

    [Test]
    public void Content_key_attach()
    {
        byte[] code = Unique(LoopCode(), 0x0a);
        IlEvm.IlContract contract = Ship(code)[0];
        RunCounter runs = CountRuns(contract);
        Outcome expected = Interpret(code, LoopInput);

        // Code no cache stamped, as a state override's: found among the shipped codes as long, compared byte for byte, and
        // run compiled. Nothing on the frame's path takes its keccak, nor a hash of its content: a state override's code
        // info is new on every call its code cache misses.
        long hashes = IlRuntime.ContentHashes;
        CodeInfo overrideCode = new(code);
        Assert.That(Run(QueryVm, overrideCode, LoopInput), Is.EqualTo(expected));
        Assert.That(Run(QueryVm, overrideCode, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(2));
        Assert.That(StateOf(overrideCode).QueryRun, Is.SameAs(contract));
        Assert.That(StateOf(overrideCode).Profile, Is.SameAs(contract.Profile), "the shipped contract's profile, keyed by its keccak");
        Assert.That(IlRuntime.ContentHashes, Is.EqualTo(hashes), "no content hash");
        // The Legacy rules' lookup finds it the same way.
        Assert.That(IlEvm.FindPrecompiledOnly(new CodeInfo(code), Bodies(Spec)), Is.SameAs(contract));

        // A code as long with other bytes stays on the interpreter: the bytes decide, among every shipped code of a length.
        byte[] other = [.. code];
        other[^1] ^= 0xff;
        Ship(Unique(ArithmeticCode(), 0x0c));
        Assert.That(IlRuntime.ShippedLengthIndex![code.Length], Has.Length.EqualTo(1));
        CodeInfo sameLength = new(other);
        Assert.That(Run(QueryVm, sameLength, LoopInput), Is.EqualTo(Interpret(other, LoopInput)));
        Assert.That(runs.Runs, Is.EqualTo(2));
        Assert.That(IlEvm.IsCompiled(sameLength), Is.False);
        Assert.That(IlEvm.FindPrecompiledOnly(new CodeInfo(other), Bodies(Spec)), Is.Null);
        // Two shipped codes of one length are both found.
        byte[] twin = [.. other];
        IlEvm.IlContract second = Ship(twin)[0];
        Assert.That(IlRuntime.ShippedLengthIndex![code.Length], Has.Length.EqualTo(2));
        Assert.That(Run(QueryVm, new CodeInfo(twin), LoopInput), Is.EqualTo(Interpret(twin, LoopInput)));
        Assert.That(IlRuntime.ShippedByContent(new CodeInfo(twin)), Is.SameAs(second.Profile));
        Assert.That(IlRuntime.ShippedByContent(new CodeInfo(code)), Is.SameAs(contract.Profile));

        // Block processing hashes nothing: its code comes stamped, and code that does not is left alone.
        hashes = IlRuntime.ContentHashes;
        CodeInfo unstamped = new(code);
        Assert.That(OnBlockThread(() => Run(BlockVm, unstamped, LoopInput)), Is.EqualTo(expected));
        Assert.That(IlRuntime.ContentHashes, Is.EqualTo(hashes));
        Assert.That(IlEvmFrameRunner.CodeSlot(unstamped), Is.SameAs(IlRuntime.QuietBlock));
        Assert.That(runs.Runs, Is.EqualTo(2));
    }

    [Test]
    public void Initcode_never_resolved()
    {
        // Initcode shipped and stamped, as it never is, and a counting runner: still never looked up, hashed or run compiled.
        byte[] init = Unique(InitCode(), 0x0b);
        IlEvm.IlContract contract = Ship(init)[0];
        RunCounter runs = CountRuns(contract);
        IlRuntime.QueryCounting = true;
        long hashes = IlRuntime.ContentHashes, lookups = IlRuntime.Lookups;
        CodeInfo stamped = Stamped(init);
        CodeInfo unstamped = new(init);
        Outcome expected = Interpret(init, [], ExecutionType.CREATE);
        Assume.That(expected.Error, Is.False, "the initcode deploys");
        Assert.That(Run(QueryVm, unstamped, [], type: ExecutionType.CREATE), Is.EqualTo(expected));
        Assert.That(Run(QueryVm, stamped, [], type: ExecutionType.CREATE), Is.EqualTo(expected));
        Assert.That(IlEvmFrameRunner.CodeSlot(unstamped), Is.SameAs(IlRuntime.QuietQuery));
        Assert.That(OnBlockThread(() => Run(BlockVm, stamped, [], type: ExecutionType.CREATE)), Is.EqualTo(expected));
        Assert.That(IlEvmFrameRunner.CodeSlot(stamped), Is.SameAs(IlRuntime.QuietBlock));
        Assert.That(runs.Runs, Is.EqualTo(0));
        Assert.That((IlRuntime.ContentHashes, IlRuntime.Lookups), Is.EqualTo((hashes, lookups)));

        // The same code called rather than created runs compiled.
        Assert.That(Run(QueryVm, stamped, []), Is.EqualTo(Interpret(init, [])));
        Assert.That(runs.Runs, Is.EqualTo(1));
    }

    [Test]
    public void Fork_miss()
    {
        OpcodeBodySet osaka = Bodies(Spec), prague = Bodies(Prague.Instance);
        Assume.That(prague, Is.Not.SameAs(osaka), "Prague and Osaka must run different bodies for this test");
        byte[] code = Unique(LoopCode(), 0x0c);
        IlEvm.IlContract shipped = Ship(code)[0];
        RunCounter runs = CountRuns(shipped);
        CodeInfo codeInfo = Stamped(code);
        Outcome expected = Interpret(code, LoopInput);
        Outcome expectedPrague = Interpret(code, LoopInput, vm: PragueQueryVm);

        // A frame of another fork's body set runs on the interpreter; the state keeps the contract for its own.
        Assert.That(Run(PragueQueryVm, codeInfo, LoopInput), Is.EqualTo(expectedPrague));
        Assert.That(runs.Runs, Is.EqualTo(0));
        IlEvm.IlCodeState state = StateOf(codeInfo);
        Assert.That(state.QueryRun, Is.SameAs(shipped));
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(1));

        // The body set it found nothing for goes to the interpreter at once from then: no look again, until something changes.
        long misses = IlRuntime.ForkMisses;
        Assert.That(Run(PragueQueryVm, codeInfo, LoopInput), Is.EqualTo(expectedPrague));
        Assert.That((IlRuntime.ForkMisses - misses, state.QueryMissed), Is.EqualTo((0L, prague)));
        Assert.That(shipped.Profile!.LastBodies, Is.SameAs(prague), "the body set its code runs on now, which a live compile would be for");

        // A contract compiled ahead for the next fork attaches at its first part there, with no new version, though parts
        // of that body set found nothing before it was published.
        IlEvm.IlContract next = Compile(code, Prague.Instance);
        next.Origin = IlOrigin.Shipped;
        next.RunMask = IlRuntime.RunAll;
        RunCounter nextRuns = CountRuns(next);
        int version = IlRuntime.Version;
        IlRuntime.PublishNext(shipped.Profile!, next);
        Assert.That(Run(PragueQueryVm, codeInfo, LoopInput), Is.EqualTo(expectedPrague));
        Assert.That(nextRuns.Runs, Is.EqualTo(1));
        Assert.That(IlRuntime.Version, Is.EqualTo(version));
        Assert.That(state.QueryRun, Is.SameAs(next));

        // And back, for a part of the first fork.
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(2));
        Assert.That(state.QueryRun, Is.SameAs(shipped));
    }

    [Test]
    public void A_frame_on_another_body_set_is_the_code_s_latest_for_live_profiling()
    {
        // Code nothing compiles, counted by the queries' runner: a part on Prague's body set tells the profile the body
        // set its code runs on now, which a live compile would be for, though no state resolved again.
        OpcodeBodySet osaka = Bodies(Spec), prague = Bodies(Prague.Instance);
        Assume.That(prague, Is.Not.SameAs(osaka));
        IlRuntime.QueryCounting = true;
        byte[] code = Unique(LoopCode(), 0x18);
        CodeInfo codeInfo = Stamped(code);
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(Interpret(code, LoopInput)));
        IlCodeProfile profile = StateOf(codeInfo).Profile!;
        Assert.That(profile.LastBodies, Is.SameAs(osaka));
        int version = IlRuntime.Version;
        Assert.That(Run(PragueQueryVm, codeInfo, LoopInput), Is.EqualTo(Interpret(code, LoopInput, vm: PragueQueryVm)));
        Assert.That((profile.LastBodies, IlRuntime.Version), Is.EqualTo((prague, version)));
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(Interpret(code, LoopInput)));
        Assert.That(profile.LastBodies, Is.SameAs(osaka), "and back");
    }

    [Test]
    public void A_released_live_contract_lets_go_of_its_tables_and_its_frames_hand_back()
    {
        byte[] code = Unique(LoopCode(), 0x19);
        IlEvm.IlContract live = Promote(code, IlOrigin.LiveRpc);
        RunCounter runs = CountRuns(live);
        CodeInfo codeInfo = Stamped(code);
        Outcome expected = Interpret(code, LoopInput);
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(1));
        IlEvm.IlCodeState state = StateOf(codeInfo);

        // Released, its tables go with its code: states and threads' visits may hold it until they next resolve.
        IlCheck.ReleaseNow(live);
        Assert.That((live.IsReleased, live.TablesReleased, live.Runners.All(static r => r is null)), Is.EqualTo((true, true, true)));
        // A part that read the state before it resolved again, as one under way does: it hands back at its entry, and the
        // interpreter runs it.
        state.Version = IlRuntime.Version;
        Assert.That(state.QueryRun, Is.SameAs(live));
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(1));

        // A contract the Legacy rules compiled keeps its tables when released, as before.
        IlEvm.IlContract legacy = Compile(Unique(LoopCode(), 0x1a), Spec);
        legacy.Release();
        Assert.That((legacy.IsReleased, legacy.TablesReleased), Is.EqualTo((true, false)));
    }

    [Test]
    public void A_code_state_s_adaptive_fields_share_a_line_with_its_header()
    {
        // The fields every part of code a state holds reads, just past the object's header: the line its type check reads.
        IlEvm.IlCodeState state = new();
        Assert.That(Offset(state, ref state.Version), Is.EqualTo(0));
        Assert.That(Offset(state, ref Unsafe.AsRef(in state.GateHash)), Is.EqualTo(4));
        // Only the fields' addresses are taken, never read through.
#pragma warning disable CS0420
        Assert.That(Offset(state, ref state.BlockRun), Is.EqualTo(8));
        Assert.That(Offset(state, ref state.QueryRun), Is.EqualTo(16));
#pragma warning restore CS0420
        Assert.That(Offset(state, ref state.Profile), Is.EqualTo(24));
        Assert.That(Offset(state, ref state.BlockMissed), Is.EqualTo(32));
    }

    /// <summary>A field's offset from an object's first field.</summary>
    private static long Offset<T>(object owner, ref T field) => Unsafe.ByteOffset(ref Unsafe.As<RawObject>(owner).Data, ref Unsafe.As<T, byte>(ref field));

    /// <summary>An object's first field, through which its fields' offsets are measured.</summary>
    private sealed class RawObject
    {
#pragma warning disable CS0649 // never written: only its address is taken
        public byte Data;
#pragma warning restore CS0649
    }

    [Test]
    public void Block_decline_all_attaches_the_runner_and_runs_nothing_compiled_in_blocks()
    {
        IlEvmSettings.BlockDeclineAll = true;
        byte[] code = Unique(LoopCode(), 0x0d);
        IlEvm.IlContract contract = Ship(code)[0];
        RunCounter runs = CountRuns(contract);
        CodeInfo codeInfo = Stamped(code);
        Outcome expected = Interpret(code, LoopInput);
        Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, LoopInput)), Is.EqualTo(expected));
        Assert.That(Run(BlockVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(0));
        Assert.That(IlEvmFrameRunner.CodeSlot(codeInfo), Is.SameAs(IlRuntime.QuietBlock), "declined from the slot's mark, the hook's least");
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(1), "read-only queries still run it");
    }

    [Test]
    public void Rules_switched_within_one_process_each_take_the_slots_the_other_left()
    {
        // The Adaptive rules mark code nothing shipped; the Legacy rules then compile it at its first frame all the same.
        byte[] code = Unique(LoopCode(), 0x16);
        CodeInfo codeInfo = Stamped(code);
        Outcome expected = Interpret(code, LoopInput);
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(IlEvmFrameRunner.CodeSlot(codeInfo), Is.SameAs(IlRuntime.QuietAll));
        IlEvmSettings.Mode = IlMode.Legacy;
        IlEvmSettings.Threshold = 1;
        IlEvmSettings.CompileSynchronously = true;
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(IlEvm.IsCompiled(codeInfo), Is.True, "the Legacy rules' state replaced the mark");

        // Back under the Adaptive rules the state resolves again and is kept, with nothing to run: the code is not shipped.
        IlEvmSettings.Mode = IlMode.Adaptive;
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        IlEvm.IlCodeState state = StateOf(codeInfo);
        Assert.That((state.Version, state.QueryRun, state.Contract is null), Is.EqualTo((IlRuntime.Version, (IlEvm.IlContract?)null, false)));
    }

    [Test]
    public void A_counting_runner_keeps_a_profile_per_code_and_the_bytes_of_unstamped_code()
    {
        IlRuntime.QueryCounting = true;
        byte[] code = Unique(ArithmeticCode(), 0x0e);
        Outcome expected = Interpret(code, ArithmeticInput);
        CodeInfo codeInfo = Stamped(code);
        Assert.That(Run(QueryVm, codeInfo, ArithmeticInput), Is.EqualTo(expected));
        IlEvm.IlCodeState state = StateOf(codeInfo);
        IlCodeProfile profile = state.Profile!;
        Assert.That(profile, Is.Not.Null);
        Assert.That((profile.Key, profile.Size, profile.Retained.Length), Is.EqualTo((IlProfileKey.ByHash(ValueKeccak.Compute(code), code.Length), code.Length, 0)));
        Assert.That(profile.LastBodies, Is.SameAs(Bodies(Spec)));
        Assert.That(state.QueryRun, Is.Null, "counted, not compiled");
        // Another code info of the same code shares the profile.
        CodeInfo again = Stamped(code);
        Assert.That(Run(QueryVm, again, ArithmeticInput), Is.EqualTo(expected));
        Assert.That(StateOf(again).Profile, Is.SameAs(profile));

        // Code no cache stamped is keyed by its content and keeps its bytes for a live compile.
        byte[] overrideBytes = Unique(ArithmeticCode(), 0x0f);
        long retained = IlRuntime.RetainedBytes;
        CodeInfo overrideCode = new(overrideBytes);
        Assert.That(Run(QueryVm, overrideCode, ArithmeticInput), Is.EqualTo(Interpret(overrideBytes, ArithmeticInput)));
        IlCodeProfile byContent = StateOf(overrideCode).Profile!;
        Assert.That(byContent.Key.IsByContent, Is.True);
        Assert.That(byContent.Retained.Span.SequenceEqual(overrideBytes), Is.True);
        Assert.That(IlRuntime.RetainedBytes - retained, Is.EqualTo(overrideBytes.Length));

        // Past the retained budget a profile keeps no bytes; past the table's cap code gets no profile, and is declined.
        IlRuntime.MaxRetainedBytes = IlRuntime.RetainedBytes;
        CodeInfo overBudget = new(Unique(ArithmeticCode(), 0x10));
        Run(QueryVm, overBudget, ArithmeticInput);
        Assert.That(StateOf(overBudget).Profile!.Retained.IsEmpty, Is.True);
        IlRuntime.MaxProfiles = IlRuntime.ProfileCount;
        CodeInfo overCap = Stamped(Unique(ArithmeticCode(), 0x11));
        Assert.That(Run(QueryVm, overCap, ArithmeticInput), Is.EqualTo(Interpret(Unique(ArithmeticCode(), 0x11), ArithmeticInput)));
        Assert.That(IlEvmFrameRunner.CodeSlot(overCap), Is.InstanceOf<IlQuiet>(), "no state with nothing to run or count");

        // Block processing, not counting, profiles nothing.
        CodeInfo blockCode = Stamped(Unique(ArithmeticCode(), 0x12));
        OnBlockThread(() => Run(BlockVm, blockCode, ArithmeticInput));
        Assert.That(IlEvmFrameRunner.CodeSlot(blockCode), Is.SameAs(IlRuntime.QuietAll));
    }
}
