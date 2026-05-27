// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
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
using Nethermind.StatsAnalyzer.Plugin.Tracer.Pattern;
using Nethermind.StatsAnalyzer.Plugin.Types;
using NUnit.Framework;
using Testably.Abstractions.Testing;

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
        IBlocksConfig blocksConfig = new BlocksConfig { ParallelExecution = false };
        (string fileName, PatternAnalyzerFileTracer tracer) = BuildTracer(blocksConfig, "seq.json");

        RunSingleBlock(tracer, bal: new());

        Assert.That(_fileSystem.File.ReadAllText(fileName), Is.EqualTo(SingleBlockExpected));
    }

    [Test]
    public void RecordsBlock_WhenPreAmsterdamFixture()
    {
        IBlocksConfig blocksConfig = new BlocksConfig { ParallelExecution = true };
        (string fileName, PatternAnalyzerFileTracer tracer) = BuildTracer(blocksConfig, "pre-amsterdam.json");

        RunSingleBlock(tracer, bal: null);

        Assert.That(_fileSystem.File.ReadAllText(fileName), Is.EqualTo(SingleBlockExpected));
    }

    [Test]
    public void SkipsBlock_WhenParallelBalActive()
    {
        IBlocksConfig blocksConfig = new BlocksConfig { ParallelExecution = true };
        (string fileName, PatternAnalyzerFileTracer tracer) = BuildTracer(blocksConfig, "parallel.json");

        RunSingleBlock(tracer, bal: new());

        // Skipped write must leave the seed content intact.
        Assert.That(_fileSystem.File.ReadAllText(fileName), Is.EqualTo(InitialFileContent));
    }

    [Test]
    public void SkipFlagResetsAcrossBlocks()
    {
        IBlocksConfig cfg = new BlocksConfig { ParallelExecution = true };
        (string _, PatternAnalyzerFileTracer tracer) = BuildTracer(cfg, "gating.json");

        Block sequentialBlock = BuildBlock(bal: null);
        Block parallelBlock = BuildBlock(bal: new());

        tracer.StartNewBlockTrace(sequentialBlock);
        Assert.That(ReadTracerSkip(tracer), Is.False,
            "no BAL → not parallel → record");

        tracer.StartNewBlockTrace(parallelBlock);
        Assert.That(ReadTracerSkip(tracer), Is.True,
            "BAL present + parallel config → skip");

        tracer.StartNewBlockTrace(sequentialBlock);
        Assert.That(ReadTracerSkip(tracer), Is.False,
            "skip flag must reset on next sequential block");
    }

    private Block BuildBlock(ReadOnlyBlockAccessList? bal)
    {
        ForkActivation forkActivation = MainnetSpecProvider.PragueActivation;
        byte[] code = Prepare.EvmCode.PushData(0).Done;
        (Block? block, Transaction _) = PrepareTx(forkActivation, 100000, code);
        block!.BlockAccessList = bal;
        return block;
    }

    // Reflective read avoids widening public/internal surface for tests.
    private static bool ReadTracerSkip(PatternAnalyzerFileTracer tracer)
    {
        FieldInfo tracerField =
            typeof(Nethermind.StatsAnalyzer.Plugin.Tracer.StatsAnalyzerFileTracer<PatternAnalyzerTxTrace, PatternStatsAnalyzerTxTracer>)
                .GetField("Tracer", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Tracer field not found on StatsAnalyzerFileTracer");
        object current = tracerField.GetValue(tracer)
            ?? throw new InvalidOperationException("Tracer field is null");
        FieldInfo skipField =
            typeof(Nethermind.StatsAnalyzer.Plugin.Tracer.StatsAnalyzerTxTracer<Instruction, PatternStat, PatternAnalyzerTxTrace>)
                .GetField("Skip", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Skip field not found on StatsAnalyzerTxTracer");
        return (bool)skipField.GetValue(current)!;
    }

    private (string fileName, PatternAnalyzerFileTracer tracer) BuildTracer(
        IBlocksConfig blocksConfig, string filename)
    {
        string fileName = _fileSystem.Path.Combine(".", filename);
        _fileSystem.File.WriteAllText(fileName, InitialFileContent);

        CmSketch sketch = new CmSketchBuilder().SetBuckets(1000).SetHashFunctions(4).Build();
        PatternStatsAnalyzer analyzer = new StatsAnalyzerBuilder()
            .SetBufferSizeForSketches(2).SetTopN(100).SetCapacity(100000)
            .SetMinSupport(1).SetSketchResetOrReuseThreshold(0.001).SetSketch(sketch).Build();

        PatternAnalyzerFileTracer tracer = new(
            [], 1, 100, analyzer,
            [], _fileSystem, _logger, 1,
            ProcessingMode.Sequential, SortOrder.Descending,
            fileName, CancellationToken.None, blocksConfig);

        return (fileName, tracer);
    }

    private void RunSingleBlock(PatternAnalyzerFileTracer tracer, ReadOnlyBlockAccessList? bal)
    {
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
