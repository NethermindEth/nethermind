// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using System.Threading;
using Nethermind.Core.Resettables;
using Nethermind.Evm;
using Nethermind.Evm.Test;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.PatternAnalyzer.Plugin.Analyzer.Pattern;
using Nethermind.PatternAnalyzer.Plugin.Types;
using Nethermind.Specs;
using NUnit.Framework;
using PatternAnalyzerFileTracer = Nethermind.StatsAnalyzer.Plugin.Tracer.Pattern.PatternAnalyzerFileTracer;

namespace Nethermind.StatsAnalyzer.Plugin.Test;

[TestFixture]
public class PatternAnalyzerFileTracerTests : VirtualMachineTestsBase
{
    private MockFileSystem _fileSystem;
    private ILogger _logger;
    private PatternStatsAnalyzer _patternStatsAnalyzer;
    private PatternAnalyzerFileTracer _tracer;
    private PatternStatsAnalyzer _patternStatsAnalyzerIgnore;
    private PatternAnalyzerFileTracer _tracerIgnore;
    private string _testFileName;
    private string _testIgnoreFileName;
    private readonly HashSet<Instruction> _ignoreSet = new() { Instruction.JUMPDEST, Instruction.JUMP };

    public override void Setup()

    {
        base.Setup();
        _fileSystem = new MockFileSystem();
        ILogManager logManager = LimboLogs.Instance;
        var mockFileData = new MockFileData("File content");
        _testFileName = _fileSystem.Path.Combine(".", "test_opcode_stats.json");
        _testIgnoreFileName = _fileSystem.Path.Combine(".", "test_opcode_stats_ignore.json");
        _fileSystem.AddDirectory(_fileSystem.Path.GetTempPath());
        _fileSystem.AddFile(_testFileName, mockFileData);
        _fileSystem.AddFile(_testIgnoreFileName, mockFileData);
        _logger = logManager.GetClassLogger();

        var sketch = new CmSketchBuilder().SetBuckets(1000).SetHashFunctions(4).Build();
        _patternStatsAnalyzer = new StatsAnalyzerBuilder().SetBufferSizeForSketches(2).SetTopN(100).SetCapacity(100000)
            .SetMinSupport(1).SetSketchResetOrReuseThreshold(0.001).SetSketch(sketch).Build();

        var sketch2 = new CmSketchBuilder().SetBuckets(1000).SetHashFunctions(4).Build();
        _patternStatsAnalyzerIgnore = new StatsAnalyzerBuilder().SetBufferSizeForSketches(2).SetTopN(100)
            .SetCapacity(100000)
            .SetMinSupport(1).SetSketchResetOrReuseThreshold(0.001).SetSketch(sketch2).Build();

        _tracer = new PatternAnalyzerFileTracer(new ResettableList<Instruction>(), 1, 100,
            _patternStatsAnalyzer, new HashSet<Instruction>(), _fileSystem,
            _logger, 1, ProcessingMode.Sequential, SortOrder.Descending, _testFileName, CancellationToken.None);
        _tracerIgnore = new PatternAnalyzerFileTracer(new ResettableList<Instruction>(), 1, 100,
            _patternStatsAnalyzerIgnore, _ignoreSet, _fileSystem, _logger, 1,
            ProcessingMode.Sequential,
            SortOrder.Descending, _testIgnoreFileName, CancellationToken.None);
    }


    private void ExecuteTransactions(byte[][] codes, PatternAnalyzerFileTracer tracer)
    {
        var forkActivation = MainnetSpecProvider.PragueActivation;
        var (block, _) = PrepareTx(forkActivation, 100000, codes[0]);
        tracer.StartNewBlockTrace(block);
        foreach (var code in codes)
        {
            var (_, transaction) = PrepareTx(forkActivation, 100000, code);
            ITxTracer txTracer = tracer.StartNewTxTrace(transaction);
            _processor.Execute(transaction, new BlockExecutionContext(block.Header, Spec), txTracer);
            tracer.EndTxTrace();
        }

        tracer.EndBlockTrace();
        tracer.CompleteAllTasks();
    }

    [TestCaseSource(nameof(GetTraceCases))]
    public void Test_File_Tracer(byte[][] codes, string expectedTrace)
    {
        ExecuteTransactions(codes, _tracer);
        var fileContent = _fileSystem.File.ReadAllText(_testFileName);
        Assert.That(fileContent, Is.Not.Empty); //IsNotEmpty(fileContent);
        Assert.That(fileContent, Is.EqualTo(expectedTrace),
            $"\n --- Found: {fileContent} \n Expected: {expectedTrace}");
    }

    [TestCaseSource(nameof(GetIgnoreSetCases))]
    public void Test_File_Tracer_With_Ignore_Set(byte[][] codes, string expectedTrace)
    {
        ExecuteTransactions(codes, _tracerIgnore);
        var fileContent = _fileSystem.File.ReadAllText(_testIgnoreFileName);
        Assert.That(fileContent, Is.Not.Empty); //IsNotEmpty(fileContent);
        Assert.That(fileContent, Is.EqualTo(expectedTrace),
            $"\n --- Found: {fileContent} \n Expected: {expectedTrace}");
    }

    private static IEnumerable<TestCaseData> GetIgnoreSetCases()
    {
        yield return new TestCaseData(
            new[]
            {
                Prepare.EvmCode.JUMPDEST().PushData(6).JUMP().JUMPDEST().JUMPDEST().JUMPDEST().JUMPDEST().JUMPDEST()
                    .PushData(0).PushData(0).STOP().Done
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.006,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":1}]}"""
        );
        yield return new TestCaseData(
            new[]
            {
                Prepare.EvmCode.JUMPDEST().PushData(6).JUMP().PushData(6).JUMPDEST().PushData(0).PushData(0).STOP()
                    .Done
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.006,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":1}]}"""
        );
        yield return new TestCaseData(
            new[]
            {
                Prepare.EvmCode.JUMPDEST().PushData(0).JUMPDEST().PushData(6).JUMPDEST().PushData(0).Done
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.006,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":1}]}"""
        );
        yield return new TestCaseData(
            new[]
            {
                Prepare.EvmCode.PushData(0).PushData(6).JUMPDEST().PushData(0).Done
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.006,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":1}]}"""
        );
    }

    private static IEnumerable<TestCaseData> GetTraceCases()
    {
        yield return new TestCaseData(
            new[]
            {
                Prepare.EvmCode.PushData(10).Done
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0,"confidence":0.9375,"stats":[]}"""
        );
        yield return new TestCaseData(
            new[]
            {
                Prepare.EvmCode.PUSHx([1, 2, 3, 4, 5]).Done
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0,"confidence":0.9375,"stats":[]}"""
        );
        yield return new TestCaseData(
            new[]
            {
                Prepare.EvmCode.PushData(10).PushData(12).Done
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.002,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":1}]}"""
        );
        yield return new TestCaseData(
            new[]
            {
                Prepare.EvmCode.PUSHx([1, 2, 3, 4, 5]).PUSHx([3, 4]).Done
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.002,"confidence":0.9375,"stats":[{"pattern":"PUSH5 PUSH2","bytes":[100,97],"count":1}]}"""
        );
        yield return new TestCaseData(
            new[]
            {
                Prepare.EvmCode.PushData(0).PushData(0).PushData(0).Done
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.006,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":1}]}"""
        );
        yield return new TestCaseData(
            new[]
            {
                Prepare.EvmCode.PushData(0).PushData(0).PushData(0).Done,
                Prepare.EvmCode.PushData(0).Done
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.006,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":1}]}"""
        );
        yield return new TestCaseData(
            new[]
            {
                Prepare.EvmCode.PushData(0).PushData(0).PushData(0).Done,
                Prepare.EvmCode.PushData(0).PushData(0).PushData(0).Done
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.012,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":4},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":2}]}"""
        );
        yield return new TestCaseData(
            new[]
            {
                Prepare.EvmCode.PushData(0).PushData(0).PushData(0).PushData(0).PushData(0).PushData(0).Done
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.03,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":5},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":4},{"pattern":"PUSH1 PUSH1 PUSH1 PUSH1","bytes":[96,96,96,96],"count":3},{"pattern":"PUSH1 PUSH1 PUSH1 PUSH1 PUSH1","bytes":[96,96,96,96,96],"count":2},{"pattern":"PUSH1 PUSH1 PUSH1 PUSH1 PUSH1 PUSH1","bytes":[96,96,96,96,96,96],"count":1}]}"""
        );
    }
}
