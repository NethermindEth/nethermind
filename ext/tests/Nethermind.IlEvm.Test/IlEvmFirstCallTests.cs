// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.IlEvm.Test;

/// <summary>
/// A node's first calls: the RPC list shipped with the plugin, whose entries carry their code, compiled at startup with
/// no code database and found by content; the visit gate a list entry's <c>qminv=</c> seeds; the queries' runner's check
/// of a shipped contract, off in the start-up window; and the ahead copies, whose helpers are optimized from their first
/// call, until the switch to the contracts' own code.
/// </summary>
[NonParallelizable]
public class IlEvmFirstCallTests : IlEvmAdaptiveFixture
{
    private static byte[] Hex(string hex) => Bytes.FromHexString(hex);

    /// <summary>A list line for <paramref name="code"/> as the RPC list writes one: its keccak, size, no block thread, every visit admitted, and the code.</summary>
    private static string RpcLine(byte[] code, string? hash = null) =>
        $"{hash ?? ValueKeccak.Compute(code).ToString()} size={code.Length} use=- qminv=0 code={code.ToHexString(true)}";

    [Test]
    public void The_shipped_rpc_list_carries_its_contracts_code()
    {
        Assume.That(IlEvmPrecompileList.Overridden, Is.False, "an override replaces the lists");
        IlEvmPrecompileList.ListFile rpc = IlEvmPrecompileList.LoadRpcList(out string origin);
        Assert.That(origin, Is.EqualTo("Rpc"));
        Assert.That(rpc.Entries, Has.Count.InRange(1, 64));
        foreach (IlEvmPrecompileList.Entry entry in rpc.Entries)
        {
            Assert.That(entry.Code, Is.Not.Null, entry.Hash.ToString());
            Assert.That(ValueKeccak.Compute(entry.Code!), Is.EqualTo(entry.Hash), "each code is its hash's");
            Assert.That(entry.Size, Is.EqualTo(entry.Code!.Length));
            // The queries' runner runs every visit compiled from the first call; block processing, which never meets
            // state-override code, none.
            Assert.That((entry.Use, entry.QueryMinVisitLog2), Is.EqualTo(((IlEvmUse?)IlEvmUse.None, (int?)0)));
        }

        // Embedded compressed, below the bytes of code it carries, let alone their hex.
        using Stream? resource = typeof(IlEvmPrecompileList).Assembly.GetManifestResourceStream(IlEvmPrecompileList.RpcResourceName);
        Assert.That(resource, Is.Not.Null);
        Assert.That(resource!.Length, Is.LessThan(rpc.Entries.Sum(static e => e.Code!.Length)));
        // The block list's entries carry none, and the lists share none.
        IlEvmPrecompileList.ListFile blocks = IlEvmPrecompileList.LoadList(IlEvmPrecompileSource.Shipped, out _);
        Assert.That(blocks.Entries.All(static e => e.Code is null), Is.True);
        Assert.That(blocks.Entries.Select(static e => e.Hash).Intersect(rpc.Entries.Select(static e => e.Hash)), Is.Empty);
    }

    /// <summary>A startup step on an empty code database, at <paramref name="head"/>, with a list file of <paramref name="list"/> or the embedded lists for null; its log lines.</summary>
    private List<string> Startup(IlEvmConfig config, IEnumerable<string>? list, ulong head = 5)
    {
        IDbProvider dbProvider = TestMemDbProvider.Init();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(head).WithTimestamp(1_700_000_000).TestObject);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Spec);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"ilevm-first-call-list-{Environment.ProcessId}.txt");
        string? saved = Environment.GetEnvironmentVariable(IlEvmPrecompileList.OverrideVariable);
        if (list is not null)
        {
            File.WriteAllLines(path, list);
            Environment.SetEnvironmentVariable(IlEvmPrecompileList.OverrideVariable, path);
        }
        else
        {
            Environment.SetEnvironmentVariable(IlEvmPrecompileList.OverrideVariable, null);
        }

        Nethermind.Core.Test.TestLogger logger = new();
        IMainProcessingContext context = Substitute.For<IMainProcessingContext>();
        context.BranchProcessor.Returns(Substitute.For<IBranchProcessor>());
        try
        {
            using CancellationTokenSource exit = new();
            new IlEvmStartupStep(config, dbProvider, blockTree, specProvider, new Lazy<IMainProcessingContext>(() => context),
                new BlocksConfig(), new ExitSource(exit.Token), new OneLogger(logger)).Execute(default).Wait();
        }
        finally
        {
            Environment.SetEnvironmentVariable(IlEvmPrecompileList.OverrideVariable, saved);
            File.Delete(path);
        }

        return logger.LogList;
    }

    private static string PrecompileLine(List<string> lines) => lines.Single(static l => l.StartsWith("IL-EVM precompile:", StringComparison.Ordinal));

    [Test]
    public void Rpc_entries_compile_from_their_code_with_no_code_database()
    {
        IlEvmSettings.Gate = true;
        byte[] code = Unique(LoopCode(), 0x61), other = Unique(ArithmeticCode(), 0x62);
        List<string> lines = Startup(new IlEvmConfig { Selection = IlEvmSelection.Adaptive, Enabled = true }, [RpcLine(code), RpcLine(other), $"{ValueKeccak.Compute(Unique(LoopCode(), 0x63))}"]);
        Assert.That(PrecompileLine(lines), Does.StartWith("IL-EVM precompile: 2 of 3 shipped contracts compiled").And.Contain("; 1 not in the code database, 0 declined, ")
            .And.Contain(", 2 with an ahead copy for the first calls, "));
        Assert.That((IlShippedBacklog.Compiled, IlShippedBacklog.MissingCount), Is.EqualTo((2, 1)));
        Assert.That(IlWarmUp.AheadArmed, Is.EqualTo(2));

        // Override code, which no cache stamps: found by its content, and run compiled from its first visit, on its ahead
        // copy until the switch; nothing learned of its visits, which the list vouches for.
        IlCodeProfile profile = IlRuntime.ShippedByContent(new CodeInfo(code))!;
        IlEvm.IlContract contract = profile.Shipped!;
        Assert.That((profile.QuerySeeded, profile.QueryMinVisitLog2, profile.UseMask, contract.RunMask), Is.EqualTo((true, (byte)0, IlRuntime.RunQuery, IlRuntime.RunQuery)));
        Assert.That(contract.Ahead, Is.Not.Null);
        RunCounter own = CountRuns(contract), ahead = CountRuns(contract.Ahead!);
        IlWarmUp.AheadOwnMask = int.MaxValue;
        CodeInfo overrideCode = new(code);
        Outcome expected = Interpret(code, LoopInput);
        Assert.That(Run(QueryVm, overrideCode, LoopInput), Is.EqualTo(expected));
        Assert.That((ahead.Runs, own.Runs), Is.EqualTo((1, 0)), "compiled from the first visit, on the ahead copy");
        Assert.That(profile.PeekVisits(block: false), Is.Null, "nothing to learn");
        // Its own code once the copies go; block processing never runs it.
        IlWarmUp.SwitchFromAhead();
        Assert.That(Run(QueryVm, overrideCode, LoopInput), Is.EqualTo(expected));
        Assert.That((ahead.Runs, own.Runs), Is.EqualTo((1, 1)));
        Assert.That(OnBlockThread(() => Run(BlockVm, Stamped(code), LoopInput)), Is.EqualTo(expected));
        Assert.That(own.Runs, Is.EqualTo(1));

        // No ahead copies with their budget at 0.
        IlEvm.ClearPrecompiled();
        IlShippedBacklog.Reset();
        IlWarmUp.ResetAheadForTests();
        IlWarmUp.AheadMaxMs = 0;
        lines = Startup(new IlEvmConfig { Selection = IlEvmSelection.Adaptive, Enabled = true }, [RpcLine(code)]);
        Assert.That(PrecompileLine(lines), Does.Not.Contain("ahead copy"));
        Assert.That((IlShippedBacklog.Compiled, IlWarmUp.AheadArmed, IlRuntime.ShippedByContent(new CodeInfo(code))!.Shipped!.Ahead), Is.EqualTo((1, 0, (IlEvm.IlContract?)null)));
    }

    [Test]
    public void Code_that_is_not_its_hash_never_compiles()
    {
        // A shipped contract runs wherever its keccak is met: code an entry carries under another hash is declined.
        byte[] code = Unique(LoopCode(), 0x64), claimed = Unique(LoopCode(), 0x65);
        long failures = IlEvmSettings.FailuresByReason.GetValueOrDefault(IlShippedBacklog.MismatchedCode);
        List<string> lines = Startup(new IlEvmConfig { Selection = IlEvmSelection.Adaptive, Enabled = true }, [RpcLine(code, ValueKeccak.Compute(claimed).ToString())]);
        Assert.That(PrecompileLine(lines), Does.StartWith("IL-EVM precompile: 0 of 1 shipped contracts compiled").And.Contain("; 0 not in the code database, 1 declined, "));
        Assert.That(IlEvmSettings.FailuresByReason.GetValueOrDefault(IlShippedBacklog.MismatchedCode), Is.EqualTo(failures + 1));
        Assert.That(IlEvm.IsPrecompiled(ValueKeccak.Compute(claimed)), Is.False);
        Assert.That(IlRuntime.ShippedByContent(new CodeInfo(code)), Is.Null);
        CodeInfo stamped = Stamped(claimed);
        Assert.That(Run(QueryVm, stamped, LoopInput), Is.EqualTo(Interpret(claimed, LoopInput)));
        Assert.That(IlEvm.IsCompiled(stamped), Is.False);
    }

    [Test]
    public void The_rpc_list_loads_under_the_adaptive_rules_only()
    {
        Assume.That(IlEvmPrecompileList.Overridden, Is.False, "an override replaces the lists");
        int listed = IlEvmPrecompileList.LoadRpcList(out _).Entries.Count;
        IlEvmPrecompileList.Entry first = IlEvmPrecompileList.LoadRpcList(out _).Entries[0];

        // Every scope and source, as the shipped block list: here all of the block list's code missing.
        IlEvmConfig adaptive = new() { Enabled = true, Selection = IlEvmSelection.Adaptive, Scope = IlEvmScope.Rpc, Prefer = IlEvmPrefer.None };
        IlEvmModule.Configure(adaptive);
        string line = PrecompileLine(Startup(adaptive, list: null, head: 0));
        Assert.That(line, Does.Contain($"(list Shipped and the RPC list, {listed} entries with their code; "));
        Assert.That(IlShippedBacklog.Compiled, Is.EqualTo(listed));
        Assert.That(IlRuntime.ShippedByContent(new CodeInfo(first.Code!))?.Shipped?.Ahead, Is.Not.Null);

        // Off, as an experiment arm.
        IlEvm.ClearPrecompiled();
        IlShippedBacklog.Reset();
        IlWarmUp.ResetAheadForTests();
        line = PrecompileLine(Startup(new IlEvmConfig { Selection = IlEvmSelection.Adaptive, Enabled = true, PrecompileRpc = false }, list: null));
        Assert.That(line, Does.Not.Contain("RPC list"));
        Assert.That(IlShippedBacklog.Compiled, Is.Zero);

        // The Legacy rules compile the block list alone, as before.
        IlEvm.ClearPrecompiled();
        IlShippedBacklog.Reset();
        IlEvmConfig legacy = new() { Enabled = true, Selection = IlEvmSelection.Legacy };
        IlEvmModule.Configure(legacy);
        line = PrecompileLine(Startup(legacy, list: null));
        Assert.That(line, Does.Not.Contain("RPC list").And.Not.Contain("ahead copy"));
        Assert.That(IlShippedBacklog.Compiled, Is.Zero);
        Assert.That(IlRuntime.ShippedByContent(new CodeInfo(first.Code!)), Is.Null);
    }

    [Test]
    public void Seeded_entries_run_compiled_from_the_first_visit()
    {
        IlEvmSettings.Gate = true;
        IlGate.Query = IlGate.QueryPrior;
        IlThread gate = IlThread.Current;
        gate.ResetForTests();
        byte[] seededCode = Unique(LoopCode(), 0x66), plainCode = Unique(LoopCode(), 0x67);
        IlEvm.IlContract seeded = Ship(new IlEvmPrecompileList.Entry(ValueKeccak.Compute(seededCode), Use: IlEvmUse.None, QueryMinVisitLog2: 0), seededCode);
        IlEvm.IlContract plain = Ship(plainCode)[0];
        RunCounter seededRuns = CountRuns(seeded), plainRuns = CountRuns(plain);
        CodeInfo seededInfo = new(seededCode), plainInfo = new(plainCode);
        for (int i = 0; i < 5; i++)
        {
            Assert.That(Run(QueryVm, seededInfo, LoopInput), Is.EqualTo(Interpret(seededCode, LoopInput)));
            Assert.That(Run(QueryVm, plainInfo, LoopInput), Is.EqualTo(Interpret(plainCode, LoopInput)));
        }

        // The seeded entry's every visit compiled from its first part, measured for nothing; the plain one's short visits
        // rent while their key is learned, and stay on the interpreter.
        Assert.That((seededRuns.Runs, plainRuns.Runs), Is.EqualTo((5, 0)));
        Assert.That(gate.Counters.VisitsSeeded, Is.EqualTo(5));
        Assert.That(gate.VisitOf(StateOf(seededInfo))!.Value.Measured, Is.False);
        Assert.That(seeded.Profile!.PeekVisits(block: false), Is.Null);
        Assert.That(plain.Profile!.PeekVisits(block: false), Is.Not.Null);
        Assert.That(IlGate.Describe(), Does.Contain(" seeded, "));
    }

    [Test]
    public void A_seeded_threshold_admits_keys_not_learned_and_holds_learned_ones_to_it()
    {
        IlEvmSettings.Gate = true;
        // No admission for a run on the same thread a moment ago: each visit here is decided by what its key learned.
        IlGate.Query = IlGate.QueryPrior.With(warmOps: 0);
        IlThread gate = IlThread.Current;
        gate.ResetForTests();
        byte[] code = Unique(LoopCode(), 0x68);
        // Visits of 2^20 opcodes and longer would run compiled from their first part; these run under a hundred.
        IlEvm.IlContract contract = Ship(new IlEvmPrecompileList.Entry(ValueKeccak.Compute(code), Use: IlEvmUse.None, QueryMinVisitLog2: 20), code);
        RunCounter runs = CountRuns(contract);
        CodeInfo codeInfo = new(code);
        Outcome expected = Interpret(code, LoopInput);
        for (int i = 0; i < IlGate.QueryPrior.MinVisits; i++)
            Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That((runs.Runs, gate.Counters.VisitsSeeded, gate.Counters.VisitsRenting), Is.EqualTo((IlGate.QueryPrior.MinVisits, (long)IlGate.QueryPrior.MinVisits, 0L)),
            "compiled while the key is learned, not rented");
        for (int i = 0; i < 16; i++)
            Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(IlGate.QueryPrior.MinVisits), "a learned short key is held to the entry's threshold");
        Assert.That(gate.Counters.VisitsSettled, Is.EqualTo(16));
    }

    [Test]
    public void Ahead_twins_mirror_the_called_twins()
    {
        MethodInfo[] methods = typeof(IlEvm).GetMethods(BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo Named(string name) => methods.Single(m => m.Name == name);
        List<MethodInfo> twins = [.. methods.Where(static m => m.Name.EndsWith("Called", StringComparison.Ordinal)), Named("IlCarry"), Named("IlBorrow"), Named("IlEmbedded")];
        Assert.That(twins, Has.Count.GreaterThan(10));
        foreach (MethodInfo twin in twins)
        {
            MethodInfo ahead = Named(twin.Name + "Ahead");
            static string Shape(MethodInfo m) => $"{m.ReturnType.Name}({string.Join(", ", m.GetParameters().Select(static p => p.ParameterType.Name))})<{m.GetGenericArguments().Length}>";
            Assert.That(Shape(ahead), Is.EqualTo(Shape(twin)), twin.Name);
            // The ahead twins optimized from the first call; the called twins tiered, profiled on calls.
            Assert.That(ahead.MethodImplementationFlags & (MethodImplAttributes.AggressiveOptimization | MethodImplAttributes.NoInlining),
                Is.EqualTo(MethodImplAttributes.AggressiveOptimization | MethodImplAttributes.NoInlining), ahead.Name);
            Assert.That(twin.MethodImplementationFlags & MethodImplAttributes.AggressiveOptimization, Is.EqualTo((MethodImplAttributes)0), twin.Name);
        }
    }

    /// <summary>
    /// Arithmetic, comparisons into a jump, memory and calldata words at constant and computed offsets, and an embedded
    /// body: every family of helper compiled code calls out of line.
    /// </summary>
    /// <remarks>
    /// CALLDATALOAD 0, CALLDATALOAD 32, MUL, PUSH1 64, MSTORE, PUSH1 64, MLOAD, PUSH1 3, ADD, PUSH1 0, MLOAD, DIV, PUSH1 10,
    /// LT, PUSH1 jump, JUMPI, PUSH1 0, SLOAD, POP, JUMPDEST, ADDRESS, BALANCE, POP, STOP.
    /// </remarks>
    private static byte[] HelpersCode() => Hex("6000356020350260405260405160030160005104600a10601e57600054505b30315000");

    [Test]
    public void An_ahead_copy_calls_the_ahead_twins_only()
    {
        // The Legacy rules keep what each chunk calls, which the Adaptive ones compile ahead and let go of.
        IlEvmSettings.Mode = IlMode.Legacy;
        byte[] code = Unique(HelpersCode(), 0x69);
        static List<MethodInfo> Helpers(IlEvm.IlContract contract) =>
            [.. Enumerable.Range(0, contract.ChunkCount).SelectMany(c => contract.CalleesOf(c) ?? []).OfType<MethodInfo>()
                .Where(static m => m.DeclaringType == typeof(IlEvm) && (m.MethodImplementationFlags & MethodImplAttributes.NoInlining) != 0)];
        List<MethodInfo> own = Helpers(Compile(code, Spec));
        IlEvm.IlContract? copy = IlEvm.PrecompileAhead(code, Bodies(Spec), out string? reason);
        Assert.That(copy, Is.Not.Null, reason);
        List<MethodInfo> ahead = Helpers(copy!);
        Assert.That(own, Is.Not.Empty);
        Assert.That(own.Where(static m => m.Name.EndsWith("Ahead", StringComparison.Ordinal)), Is.Empty);
        Assert.That(ahead.Select(static m => m.Name).Order(), Is.EqualTo(own.Select(static m => m.Name + "Ahead").Order()), "the same calls, to the ahead twins");
        Assert.That(ahead.All(static m => (m.MethodImplementationFlags & MethodImplAttributes.AggressiveOptimization) != 0), Is.True);
        // The flag is the copy's alone: a layout after it calls the called twins again.
        Assert.That(Helpers(Compile(Unique(HelpersCode(), 0x6a), Spec)).Select(static m => m.Name).Order(), Is.EqualTo(own.Select(static m => m.Name).Order()));
    }

    [Test]
    public void Frames_run_the_ahead_copy_until_the_switch()
    {
        byte[] code = Unique(HelpersCode(), 0x6b);
        IlEvm.IlContract contract = Compile(code, Spec);
        contract.Ahead = IlEvm.PrecompileAhead(code, Bodies(Spec), out _);
        IlRuntime.PublishShipped([(new IlEvmPrecompileList.Entry(ValueKeccak.Compute(code), Use: IlEvmUse.None, QueryMinVisitLog2: 0), contract)]);
        RunCounter own = CountRuns(contract), ahead = CountRuns(contract.Ahead!);
        byte[] input = new byte[64];
        input[31] = 7;
        input[63] = 3;
        CodeInfo codeInfo = new(code);
        Outcome expected = Interpret(code, input);

        // Half the parts on either code here; a node's one in AheadOwnShare on the contract's own, which tiers its helpers up.
        IlWarmUp.AheadOwnMask = 1;
        for (int i = 0; i < 64; i++)
            Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That(own.Runs + ahead.Runs, Is.EqualTo(64));
        Assert.That(own.Runs, Is.InRange(16, 48));

        IlWarmUp.ArmAhead([contract, Compile(Unique(LoopCode(), 0x6c), Spec)], LimboLogs.Instance.GetClassLogger<IlEvmFirstCallTests>());
        Assert.That(IlWarmUp.AheadArmed, Is.EqualTo(1), "only a contract with a copy");
        Assert.That(IlWarmUp.DescribeAhead(), Is.EqualTo("1 ahead copies, 0 switched to their own code"));
        Assert.That(IlWarmUp.SwitchFromAhead(1_234), Is.EqualTo(1));
        Assert.That(contract.Ahead, Is.Null);
        Assert.That(IlWarmUp.SwitchFromAhead(), Is.Zero, "once");
        int before = ahead.Runs;
        for (int i = 0; i < 8; i++)
            Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That((ahead.Runs, own.Runs + ahead.Runs), Is.EqualTo((before, 72)));
        Assert.That(IlWarmUp.DescribeAhead(), Is.EqualTo("1 ahead copies, 1 switched to their own code 1.2 s after the first frame"));
    }

    [Test]
    public void Ahead_copies_go_once_the_jit_is_quiet()
    {
        IlWarmUp.AheadMaxMs = 60_000;
        Assert.That(IlWarmUp.AheadDue(100_000, firstFrame: 0, quietSince: 50_000), Is.False, "no call yet");
        Assert.That(IlWarmUp.AheadDue(1_000 + IlWarmUp.AheadMinMs - 1, 1_000, quietSince: 1_000), Is.False, "the first calls' burst of JIT first");
        Assert.That(IlWarmUp.AheadDue(1_000 + IlWarmUp.AheadMinMs, 1_000, quietSince: -1), Is.False, "the JIT busy");
        Assert.That(IlWarmUp.AheadDue(1_000 + IlWarmUp.AheadMinMs, 1_000, quietSince: 2_500), Is.True, "the JIT quiet");
        Assert.That(IlWarmUp.AheadDue(1_000 + 60_000, 1_000, quietSince: -1), Is.True, "at the cap whatever the JIT does");
    }
}
