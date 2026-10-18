// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Container;

namespace Nethermind.IlEvm;

/// <summary>Where compiled code may run.</summary>
public enum IlEvmScope
{
    /// <summary>Only the read-only query environments of the kinds <see cref="IIlEvmConfig.Surfaces"/> names; the node's own block processing stays on the interpreter.</summary>
    Rpc,
    /// <summary>Every machine the container builds, block processing included, and every query kind whatever <see cref="IIlEvmConfig.Surfaces"/> says. Experimental.</summary>
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

[ConfigCategory(Description = "IL-EVM: compiles hot EVM contracts to native code for faster read-only calls.", HiddenFromDocs = true)]
public interface IIlEvmConfig : IConfig
{
    [ConfigItem(Description = "Enables IL-EVM.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Rpc: only the read-only query environments Surfaces names run compiled code. All (experimental): every machine the node builds, which reaches far more than queries - block processing, production, prewarming and validation, execution witness generation, Flashbots block validation and the pool's frame transaction prefix simulator - and every kind of query, whatever Surfaces says.", DefaultValue = "Rpc")]
    IlEvmScope Scope { get; set; }

    [ConfigItem(Description = "The kinds of read-only query whose environments run compiled code in Rpc scope, comma-separated: Call (eth_call, eth_estimateGas, eth_createAccessList, and the core's other users of its shared read-only call environments); Trace (debug_traceCall, debug_traceCallMany, debug_traceTransaction and its by-index and in-block variants, debug_traceBlock, debug_traceBlockByNumber, debug_traceBlockByHash, debug_traceBlockFromFile, debug_intermediateRoots, debug_standardTraceBlockToFile, debug_standardTraceBadBlockToFile, trace_call, trace_callMany, trace_rawTransaction, trace_replayTransaction, trace_replayBlockTransactions, trace_block, trace_transaction, trace_get, trace_filter and proof_getTransactionReceipt, the workers that trace a block's transactions in parallel included); Simulate (eth_simulateV1, debug_simulateV1, trace_simulateV1, and debug_traceCallMany when a bundle carries its own state or block override). All is the three, None turns the queries off. The default is Call, which keeps the debug, trace and simulate methods on the interpreter, until every one of them is benchmarked with compiled code; then it becomes All. A transaction traced opcode by opcode (the struct logger, prestateTracer, JavaScript and keccak-preimage tracers, Parity vmTrace) always runs on the interpreter; untraced transactions and system calls replayed around it run compiled.", DefaultValue = "Call")]
    ReadOnlyQueryKind Surfaces { get; set; }

    [ConfigItem(Description = "Frames a contract runs on the interpreter before it is compiled.", DefaultValue = "32")]
    int Threshold { get; set; }

    [ConfigItem(Description = "Opcodes a contract runs on the interpreter, over all its frames, before it is compiled; 0 never compiles on this count.", DefaultValue = "20000")]
    long OpcodeThreshold { get; set; }

    [ConfigItem(Description = "Interpreted opcodes per second a contract must also run before it is compiled, so rarely run code stays on the interpreter; 0 disables.", DefaultValue = "50000")]
    int HotOpcodesPerSecond { get; set; }

    [ConfigItem(Description = "Compile on the thread that makes a contract hot instead of in the background. For tests and benchmarks.", DefaultValue = "false", HiddenFromDocs = true)]
    bool CompileSynchronously { get; set; }

    [ConfigItem(Description = "Compiles the contracts of the list PrecompileList names (or of the file NETHERMIND_ILEVM_PRECOMPILE_LIST names) at startup, before blocks or calls run, so they run compiled from their first frame. Off is an experiment arm: nothing is compiled at startup.", DefaultValue = "true", HiddenFromDocs = true)]
    bool Precompile { get; set; }

    [ConfigItem(Description = "The most entries of the list compiled at startup, in list order, counted after PrecompileMinOpsPerFrame skipped its entries; 0 compiles all of them.", DefaultValue = "0", HiddenFromDocs = true)]
    int PrecompileLimit { get; set; }

    [ConfigItem(Description = "Which embedded list the startup compile reads: Shipped, the busiest contracts of the reference blocks, or Candidates, the wider list a Stats run times against the interpreter. NETHERMIND_ILEVM_PRECOMPILE_LIST, naming a file, overrides either.", DefaultValue = "Shipped", HiddenFromDocs = true)]
    IlEvmPrecompileSource PrecompileList { get; set; }

    [ConfigItem(Description = "Skips the list entries whose profile counts (ops= over frames=) give fewer opcodes per frame than this; entries without the counts are kept. 0 keeps every entry.", DefaultValue = "0", HiddenFromDocs = true)]
    int PrecompileMinOpsPerFrame { get; set; }

    [ConfigItem(Description = "In block processing (Scope=All), also compile contracts outside the shipped list once they get hot. Off, block processing runs only the shipped contracts compiled and leaves the rest to the interpreter with no bookkeeping.", DefaultValue = "false")]
    bool BlockRuntimeCompile { get; set; }

    [ConfigItem(Description = "In block processing (Scope=All), time compiled code against the interpreter and give contracts back that it does not speed up.", DefaultValue = "false")]
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
    public int Threshold { get; set; } = 32;
    public long OpcodeThreshold { get; set; } = 20_000;
    public int HotOpcodesPerSecond { get; set; } = 50_000;
    public bool CompileSynchronously { get; set; }
    public bool Precompile { get; set; } = true;
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
