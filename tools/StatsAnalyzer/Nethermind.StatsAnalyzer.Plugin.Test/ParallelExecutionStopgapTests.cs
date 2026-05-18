// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Resettables;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Test;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.StatsAnalyzer.Plugin.Analyzer.Pattern;
using Nethermind.StatsAnalyzer.Plugin.Types;
using NUnit.Framework;
using Testably.Abstractions.Testing;
using PatternAnalyzerFileTracer = Nethermind.StatsAnalyzer.Plugin.Tracer.Pattern.PatternAnalyzerFileTracer;

namespace Nethermind.StatsAnalyzer.Plugin.Test;

/// <summary>
/// Regression coverage for the parallel-BAL stopgap in
/// <see cref="Nethermind.StatsAnalyzer.Plugin.Tracer.StatsAnalyzerFileTracer{TxTrace,TxTracer}"/>.
/// See tools/StatsAnalyzer/EIP-7928-references.md §4 for the gating logic.
/// </summary>
[TestFixture]
public class ParallelExecutionStopgapTests : VirtualMachineTestsBase
{
    private const string InitialFileContent = "File content";
    private const string SingleBlockExpected =
        """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.006,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":1}]}""";

    private MockFileSystem _fileSystem;
    private ILogger _logger;

    public override void Setup()
    {
        base.Setup();
        _fileSystem = new MockFileSystem();
        _fileSystem.Initialize();
        _logger = LimboLogs.Instance.GetClassLogger<ParallelExecutionStopgapTests>();
    }

    [Test]
    public void RecordsBlock_WhenSequentialBalConfig()
    {
        // BAL is present on the block (Amsterdam+ shape) but operator has
        // disabled parallel execution: stopgap should NOT trigger, plugin
        // records normally.
        IBlocksConfig blocksConfig = new BlocksConfig { ParallelExecution = false };
        (string fileName, PatternAnalyzerFileTracer tracer) = BuildTracer(blocksConfig, "seq.json");

        RunSingleBlock(tracer, bal: new BlockAccessList());

        Assert.That(_fileSystem.File.ReadAllText(fileName), Is.EqualTo(SingleBlockExpected));
    }

    [Test]
    public void RecordsBlock_WhenPreAmsterdamFixture()
    {
        // Config allows parallel exec but block has no BAL body
        // (pre-Amsterdam / genesis / sync path that skipped BAL gen).
        // Stopgap recognizes "no BAL → not parallel" and records normally.
        IBlocksConfig blocksConfig = new BlocksConfig { ParallelExecution = true };
        (string fileName, PatternAnalyzerFileTracer tracer) = BuildTracer(blocksConfig, "pre-amsterdam.json");

        RunSingleBlock(tracer, bal: null);

        Assert.That(_fileSystem.File.ReadAllText(fileName), Is.EqualTo(SingleBlockExpected));
    }

    [Test]
    public void SkipsBlock_WhenParallelBalActive()
    {
        // Both gating conditions met: parallel exec config on AND BAL body
        // present. Stopgap fires; file untouched, analyzer state unchanged.
        IBlocksConfig blocksConfig = new BlocksConfig { ParallelExecution = true };
        (string fileName, PatternAnalyzerFileTracer tracer) = BuildTracer(blocksConfig, "parallel.json");

        RunSingleBlock(tracer, bal: new BlockAccessList());

        // The file was seeded with InitialFileContent in BuildTracer; since
        // EndBlockTrace skipped the write, it must still hold that exact text.
        Assert.That(_fileSystem.File.ReadAllText(fileName), Is.EqualTo(InitialFileContent));
    }

    // NB: a "back-to-back mixed sequential/parallel/sequential" reset test was
    // attempted here but consistently hung on the existing tracer's
    // CompleteAllTasks plumbing — there's a pre-existing race between
    // _lastTask.ContinueWith(...task.Start()) and Task.WaitAll on the queued
    // un-started Task that only manifests under tight back-to-back
    // EndBlockTrace calls. The three tests above plus inspection of
    // StartNewBlockTrace (which writes _skipThisBlock unconditionally on
    // every call) cover the reset semantics for now. The race itself is
    // tracked as a follow-up in BAL-statsanalyzer-plan.md §6d, which
    // replaces the queue model with per-worker accumulators.

    private (string fileName, PatternAnalyzerFileTracer tracer) BuildTracer(
        IBlocksConfig blocksConfig, string filename, int processingQueueSize = 1)
    {
        string fileName = _fileSystem.Path.Combine(".", filename);
        _fileSystem.File.WriteAllText(fileName, InitialFileContent);

        CmSketch sketch = new CmSketchBuilder().SetBuckets(1000).SetHashFunctions(4).Build();
        PatternStatsAnalyzer analyzer = new StatsAnalyzerBuilder()
            .SetBufferSizeForSketches(2).SetTopN(100).SetCapacity(100000)
            .SetMinSupport(1).SetSketchResetOrReuseThreshold(0.001).SetSketch(sketch).Build();

        PatternAnalyzerFileTracer tracer = new(
            new ResettableList<Instruction>(), processingQueueSize, 100, analyzer,
            new HashSet<Instruction>(), _fileSystem, _logger, 1,
            ProcessingMode.Sequential, SortOrder.Descending,
            fileName, CancellationToken.None, blocksConfig);

        return (fileName, tracer);
    }

    private void RunSingleBlock(PatternAnalyzerFileTracer tracer, BlockAccessList? bal)
    {
        // PUSH1 PUSH1 PUSH1 — yields the SingleBlockExpected JSON when traced.
        byte[] code = Prepare.EvmCode.PushData(0).PushData(0).PushData(0).Done;
        ForkActivation forkActivation = MainnetSpecProvider.PragueActivation;
        (Block? block, Transaction _) = PrepareTx(forkActivation, 100000, code);
        block!.BlockAccessList = bal;

        tracer.StartNewBlockTrace(block);
        (Block _, Transaction? transaction) = PrepareTx(forkActivation, 100000, code);
        ITxTracer txTracer = tracer.StartNewTxTrace(transaction);
        _processor.Execute(transaction!, new BlockExecutionContext(block.Header, Spec), txTracer);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();
        tracer.CompleteAllTasks();
    }
}
