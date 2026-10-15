// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using Nethermind.Config;
using Nethermind.Core.Container;

namespace Nethermind.IlEvm;

/// <summary>Where compiled code may run.</summary>
public enum IlEvmScope
{
    /// <summary>Only the environments serving read-only queries; block processing always runs on the interpreter.</summary>
    Rpc,
    /// <summary>Every machine the container builds, block processing and production included. Experimental.</summary>
    All,
}

/// <summary>Which of the lists embedded in the plugin the startup compile reads.</summary>
public enum IlEvmPrecompileSource
{
    /// <summary>The shipped list: the busiest contracts of the reference blocks.</summary>
    Shipped,
    /// <summary>The measurement candidates: a wider list, for a <see cref="IIlEvmConfig.Stats"/> run to time against the interpreter.</summary>
    Candidates,
}

/// <summary>Which rules decide what runs compiled.</summary>
public enum IlEvmSelection
{
    /// <summary>The shipped list, compiled at every startup, and what live profiling promotes between blocks; each frame part is admitted on its own.</summary>
    Adaptive,
    /// <summary>The rules before Adaptive: contracts compile at run time once hot, and block processing runs the shipped contracts on every frame. For A/B runs within one image.</summary>
    Legacy,
}

/// <summary>Which runner's frames feed live profiling, and so which load the contracts compiled between blocks are chosen for.</summary>
public enum IlEvmPrefer
{
    /// <summary>The read-only queries: an RPC node adapts to its own customers' calls.</summary>
    Rpc,
    /// <summary>Block processing, of branches of at most two blocks while not syncing; needs block processing's runner, so under the Adaptive rules it sets <see cref="IlEvmScope.All"/>.</summary>
    Blocks,
    /// <summary>Neither: only the shipped list runs compiled.</summary>
    None,
}

/// <summary>Which of block processing's threads may run compiled code.</summary>
public enum IlEvmBlockThreads
{
    /// <summary>The block-processing thread and the others the block runner serves: prewarming, production, the mempool's session.</summary>
    Both,
    /// <summary>Only the block-processing thread, as the core marks it for each processing pass.</summary>
    Main,
    /// <summary>Only the other threads the block runner serves.</summary>
    Workers,
}

[ConfigCategory(Description = "IL-EVM: compiles hot EVM contracts to native code for faster read-only calls.", HiddenFromDocs = true)]
public interface IIlEvmConfig : IConfig
{
    [ConfigItem(Description = "Enables IL-EVM.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Rpc: only read-only query environments run compiled code. All: every machine, block processing included (experimental).", DefaultValue = "Rpc")]
    IlEvmScope Scope { get; set; }

    [ConfigItem(Description = "The read-only queries accelerated in Rpc scope: Call (eth_call, eth_estimateGas, eth_createAccessList), Simulate (eth_simulateV1), Trace (debug_*/trace_* with non-instruction tracers).", DefaultValue = "Call")]
    ReadOnlyQueryKind Surfaces { get; set; }

    [ConfigItem(Description = "Adaptive: the shipped list, compiled at every startup, and what live profiling promotes between blocks run compiled, each frame part admitted on its own. Legacy: contracts compile at run time once hot, and block processing runs the shipped contracts on every frame, as before Adaptive, for A/B runs within one image.", DefaultValue = "Adaptive", HiddenFromDocs = true)]
    IlEvmSelection Selection { get; set; }

    [ConfigItem(Description = "Adaptive only: which frames feed live profiling, which compiles between blocks the contracts the node's own load runs most: Rpc, the read-only queries; Blocks, block processing (sets Scope to All); None, neither, so only the shipped list runs compiled.", DefaultValue = "Rpc")]
    IlEvmPrefer Prefer { get; set; }

    [ConfigItem(Description = "Adaptive only: runs an admitted contract's frame parts compiled only where they are expected to gain: long visits, visits warm on their thread, and visits already long on the interpreter. Off, every part of an admitted contract runs compiled.", DefaultValue = "true")]
    bool Gate { get; set; }

    [ConfigItem(Description = "Adaptive only: which of block processing's threads may run compiled code: Both, Main (the block-processing thread) or Workers (prewarming and the other threads block processing's runner serves).", DefaultValue = "Both")]
    IlEvmBlockThreads BlockThreads { get; set; }

    [ConfigItem(Description = "Adaptive only: times compiled code against the interpreter in read-only queries, in short windows, and takes compiled code away from a contract it does not speed up.", DefaultValue = "true")]
    bool Check { get; set; }

    [ConfigItem(Description = "Adaptive only: as Check, for block processing.", DefaultValue = "true")]
    bool BlockCheck { get; set; }

    [ConfigItem(Description = "How long the startup compile of the shipped list may take; the entries left compile between blocks. 0 leaves the whole list to those windows. Adaptive only.", DefaultValue = "60")]
    int StartupCompileSeconds { get; set; }

    [ConfigItem(Description = "How long after block processing finishes a branch, or the head moves, compiling between blocks may start, in milliseconds. Adaptive only.", DefaultValue = "250")]
    int QuietMs { get; set; }

    [ConfigItem(Description = "How long before the next block is expected no compile step may start, in milliseconds; must exceed the longest step. Adaptive only.", DefaultValue = "300")]
    int SlotGuardMs { get; set; }

    [ConfigItem(Description = "Threads that compile between blocks. Adaptive only: the Legacy rules keep their one background compiler.", DefaultValue = "1")]
    int CompileThreads { get; set; }

    [ConfigItem(Description = "Threads that compile while no blocks arrive, those of CompileThreads among them; 0 means a quarter of the cores, at least 1 and at most 4. Adaptive only.", DefaultValue = "0")]
    int IdleCompileThreads { get; set; }

    [ConfigItem(Description = "Adaptive only: the most IL and tables the contracts live profiling compiled may hold, per source, in MiB; MaxLiveIlBytes stays the hard cap.", DefaultValue = "128")]
    int LiveBudgetMiB { get; set; }

    [ConfigItem(Description = "Adaptive only, diagnostics: block processing's runner is attached but declines every frame, which measures the hook's own cost.", DefaultValue = "false", HiddenFromDocs = true)]
    bool BlockDeclineAll { get; set; }

    [ConfigItem(Description = "Legacy only: frames a contract runs on the interpreter before it is compiled.", DefaultValue = "32")]
    int Threshold { get; set; }

    [ConfigItem(Description = "Legacy only: opcodes a contract runs on the interpreter, over all its frames, before it is compiled; 0 never compiles on this count.", DefaultValue = "20000")]
    long OpcodeThreshold { get; set; }

    [ConfigItem(Description = "Legacy only: interpreted opcodes per second a contract must also run before it is compiled, so rarely run code stays on the interpreter; 0 disables.", DefaultValue = "50000")]
    int HotOpcodesPerSecond { get; set; }

    [ConfigItem(Description = "Legacy only: compile on the thread that makes a contract hot instead of in the background. For tests and benchmarks; the Adaptive rules compile only between blocks and ignore it.", DefaultValue = "false", HiddenFromDocs = true)]
    bool CompileSynchronously { get; set; }

    [ConfigItem(Description = "Compiles the contracts of the list PrecompileList names (or of the file NETHERMIND_ILEVM_PRECOMPILE_LIST names) at startup, before blocks or calls run, so they run compiled from their first frame. Off is an experiment arm: nothing is compiled at startup.", DefaultValue = "true", HiddenFromDocs = true)]
    bool Precompile { get; set; }

    [ConfigItem(Description = "Adaptive only: also compiles at startup the RPC list shipped with the plugin, contracts read-only calls send as state-override code, which no code database holds: each entry carries its bytecode, and its contracts run compiled from the first call. Off is an experiment arm. NETHERMIND_ILEVM_PRECOMPILE_LIST, naming a file, replaces it with the block list.", DefaultValue = "true", HiddenFromDocs = true)]
    bool PrecompileRpc { get; set; }

    [ConfigItem(Description = "The most entries of the list compiled at startup, in list order, counted after PrecompileMinOpsPerFrame skipped its entries; 0 compiles all of them.", DefaultValue = "0", HiddenFromDocs = true)]
    int PrecompileLimit { get; set; }

    [ConfigItem(Description = "Which embedded list the startup compile reads: Shipped, the busiest contracts of the reference blocks, or Candidates, the wider list a Stats run times against the interpreter. NETHERMIND_ILEVM_PRECOMPILE_LIST, naming a file, overrides either.", DefaultValue = "Shipped", HiddenFromDocs = true)]
    IlEvmPrecompileSource PrecompileList { get; set; }

    [ConfigItem(Description = "Skips the list entries whose profile counts (ops= over frames=) give fewer opcodes per frame than this; entries without the counts are kept. 0 keeps every entry.", DefaultValue = "0", HiddenFromDocs = true)]
    int PrecompileMinOpsPerFrame { get; set; }

    [ConfigItem(Description = "Legacy only: in block processing (Scope=All), also compile contracts outside the shipped list once they get hot. Off, block processing runs only the shipped contracts compiled and leaves the rest to the interpreter with no bookkeeping.", DefaultValue = "false")]
    bool BlockRuntimeCompile { get; set; }

    [ConfigItem(Description = "Legacy only: in block processing (Scope=All), time compiled code against the interpreter and give contracts back that it does not speed up.", DefaultValue = "false")]
    bool BlockSelfCheck { get; set; }

    [ConfigItem(Description = "Counts every contract's frames and interpreted opcodes instead of compiling anything, and logs the busiest at shutdown: the input for the shipped list.", DefaultValue = "false", HiddenFromDocs = true)]
    bool Profile { get; set; }

    [ConfigItem(Description = "How many of the busiest contracts the profile logs at shutdown.", DefaultValue = "4000", HiddenFromDocs = true)]
    int ProfileTop { get; set; }

    [ConfigItem(Description = "Times every contract's frame parts, interpreted and compiled, and logs per-contract totals at shutdown: the ProfileTop contracts with the most time, and every contract that ran compiled. For measurement runs: it turns the self-check off, keeps counts for every contract until shutdown, and block processing runs only the shipped contracts compiled. Block processing is timed only with Scope=All; for block-processing times two runs can compare per contract, also set Blocks.PreWarmHandoff to false.", DefaultValue = "false", HiddenFromDocs = true)]
    bool Stats { get; set; }

    [ConfigItem(Description = "With Stats, also logs the totals so far every this many blocks, tagged @<blocks>, so one run splits into a training range and a test range; 0 logs them only at shutdown.", DefaultValue = "0", HiddenFromDocs = true)]
    int StatsSnapshotBlocks { get; set; }
}

public class IlEvmConfig : IIlEvmConfig
{
    public bool Enabled { get; set; }
    public IlEvmScope Scope { get; set; } = IlEvmScope.Rpc;
    public ReadOnlyQueryKind Surfaces { get; set; } = ReadOnlyQueryKind.Call;
    public IlEvmSelection Selection { get; set; } = IlEvmSelection.Adaptive;
    public IlEvmPrefer Prefer { get; set; } = IlEvmPrefer.Rpc;
    public bool Gate { get; set; } = true;
    public IlEvmBlockThreads BlockThreads { get; set; } = IlEvmBlockThreads.Both;
    public bool Check { get; set; } = true;
    public bool BlockCheck { get; set; } = true;
    public int StartupCompileSeconds { get; set; } = 60;
    public int QuietMs { get; set; } = 250;
    public int SlotGuardMs { get; set; } = 300;
    public int CompileThreads { get; set; } = 1;
    public int IdleCompileThreads { get; set; }
    public int LiveBudgetMiB { get; set; } = 128;
    public bool BlockDeclineAll { get; set; }
    public int Threshold { get; set; } = 32;
    public long OpcodeThreshold { get; set; } = 20_000;
    public int HotOpcodesPerSecond { get; set; } = 50_000;
    public bool CompileSynchronously { get; set; }
    public bool Precompile { get; set; } = true;
    public bool PrecompileRpc { get; set; } = true;
    public int PrecompileLimit { get; set; }
    public IlEvmPrecompileSource PrecompileList { get; set; } = IlEvmPrecompileSource.Shipped;
    public int PrecompileMinOpsPerFrame { get; set; }
    public bool BlockRuntimeCompile { get; set; }
    public bool BlockSelfCheck { get; set; }
    public bool Profile { get; set; }
    public int ProfileTop { get; set; } = 4000;
    public bool Stats { get; set; }
    public int StatsSnapshotBlocks { get; set; }
}
