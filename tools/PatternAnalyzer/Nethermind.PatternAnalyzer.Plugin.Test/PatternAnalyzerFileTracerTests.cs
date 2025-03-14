// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using System.Threading;
using Nethermind.Evm;
using Nethermind.Evm.Test;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.PatternAnalyzer.Plugin.Analyzer;
using Nethermind.PatternAnalyzer.Plugin.Tracer;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.PatternAnalyzer.Plugin.Test;

[TestFixture]
public class PatternAnalyzerFileTracerTests : VirtualMachineTestsBase
{
    private MockFileSystem _fileSystem;
    private ILogger _logger;
    private StatsAnalyzer _statsAnalyzer;
    private PatternAnalyzerFileTracer _tracer;
    private StatsAnalyzer _statsAnalyzerIgnore;
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
        _statsAnalyzer = new StatsAnalyzerBuilder().SetBufferSizeForSketches(2).SetTopN(100).SetCapacity(100000)
            .SetMinSupport(1).SetSketchResetOrReuseThreshold(0.001).SetSketch(sketch).Build();

        var sketch2 = new CmSketchBuilder().SetBuckets(1000).SetHashFunctions(4).Build();
        _statsAnalyzerIgnore = new StatsAnalyzerBuilder().SetBufferSizeForSketches(2).SetTopN(100).SetCapacity(100000)
            .SetMinSupport(1).SetSketchResetOrReuseThreshold(0.001).SetSketch(sketch2).Build();

        _tracer = new PatternAnalyzerFileTracer(1, 100, _statsAnalyzer, new HashSet<Instruction>(), _fileSystem,
            _logger, 1, ProcessingMode.Sequential, SortOrder.Descending, _testFileName, CancellationToken.None);
        _tracerIgnore = new PatternAnalyzerFileTracer(1, 100, _statsAnalyzerIgnore, _ignoreSet, _fileSystem, _logger, 1,
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


        yield return new TestCaseData(
            new[]
            {
                //                   N=5     --                                  pattern appears N times                                                   --      --  Pattern Appears once    --
                Prepare.EvmCode.PushData(2).JUMPDEST().PushData(1).SSTORE().JUMPDEST().PushData(1).PushData(1).SLOAD()
                    .SUB().DUPx(1).PushData(2).JUMPI().PushData(10000).POP().STOP().Done
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.258,"confidence":0.9375,"stats":[{"pattern":"JUMPDEST PUSH1","bytes":[91,96],"count":4},{"pattern":"PUSH1 PUSH1 SLOAD","bytes":[96,96,84],"count":2},{"pattern":"SSTORE JUMPDEST","bytes":[85,91],"count":2},{"pattern":"PUSH1 SSTORE JUMPDEST","bytes":[96,85,91],"count":2},{"pattern":"JUMPDEST PUSH1 SSTORE JUMPDEST","bytes":[91,96,85,91],"count":2},{"pattern":"SSTORE JUMPDEST PUSH1","bytes":[85,91,96],"count":2},{"pattern":"PUSH1 SSTORE JUMPDEST PUSH1","bytes":[96,85,91,96],"count":2},{"pattern":"JUMPDEST PUSH1 PUSH1 SLOAD","bytes":[91,96,96,84],"count":2},{"pattern":"SSTORE JUMPDEST PUSH1 PUSH1 SLOAD","bytes":[85,91,96,96,84],"count":2},{"pattern":"PUSH1 SSTORE JUMPDEST PUSH1 PUSH1 SLOAD","bytes":[96,85,91,96,96,84],"count":2},{"pattern":"JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1 PUSH1 SLOAD","bytes":[91,96,85,91,96,96,84],"count":2},{"pattern":"SLOAD SUB","bytes":[84,3],"count":2},{"pattern":"PUSH1 SLOAD SUB","bytes":[96,84,3],"count":2},{"pattern":"PUSH1 PUSH1 SLOAD SUB","bytes":[96,96,84,3],"count":2},{"pattern":"JUMPDEST PUSH1 PUSH1 SLOAD SUB","bytes":[91,96,96,84,3],"count":2},{"pattern":"SSTORE JUMPDEST PUSH1 PUSH1 SLOAD SUB","bytes":[85,91,96,96,84,3],"count":2},{"pattern":"PUSH1 SSTORE JUMPDEST PUSH1 PUSH1 SLOAD SUB","bytes":[96,85,91,96,96,84,3],"count":2},{"pattern":"SUB DUP1","bytes":[3,128],"count":2},{"pattern":"SLOAD SUB DUP1","bytes":[84,3,128],"count":2},{"pattern":"PUSH1 SLOAD SUB DUP1","bytes":[96,84,3,128],"count":2},{"pattern":"PUSH1 PUSH1 SLOAD SUB DUP1","bytes":[96,96,84,3,128],"count":2},{"pattern":"JUMPDEST PUSH1 PUSH1 SLOAD SUB DUP1","bytes":[91,96,96,84,3,128],"count":2},{"pattern":"SSTORE JUMPDEST PUSH1 PUSH1 SLOAD SUB DUP1","bytes":[85,91,96,96,84,3,128],"count":2},{"pattern":"DUP1 PUSH1","bytes":[128,96],"count":2},{"pattern":"SUB DUP1 PUSH1","bytes":[3,128,96],"count":2},{"pattern":"SLOAD SUB DUP1 PUSH1","bytes":[84,3,128,96],"count":2},{"pattern":"PUSH1 SLOAD SUB DUP1 PUSH1","bytes":[96,84,3,128,96],"count":2},{"pattern":"PUSH1 PUSH1 SLOAD SUB DUP1 PUSH1","bytes":[96,96,84,3,128,96],"count":2},{"pattern":"JUMPDEST PUSH1 PUSH1 SLOAD SUB DUP1 PUSH1","bytes":[91,96,96,84,3,128,96],"count":2},{"pattern":"PUSH1 JUMPI","bytes":[96,87],"count":2},{"pattern":"DUP1 PUSH1 JUMPI","bytes":[128,96,87],"count":2},{"pattern":"SUB DUP1 PUSH1 JUMPI","bytes":[3,128,96,87],"count":2},{"pattern":"SLOAD SUB DUP1 PUSH1 JUMPI","bytes":[84,3,128,96,87],"count":2},{"pattern":"PUSH1 SLOAD SUB DUP1 PUSH1 JUMPI","bytes":[96,84,3,128,96,87],"count":2},{"pattern":"PUSH1 PUSH1 SLOAD SUB DUP1 PUSH1 JUMPI","bytes":[96,96,84,3,128,96,87],"count":2},{"pattern":"JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1","bytes":[91,96,85,91,96],"count":2},{"pattern":"PUSH1 SSTORE","bytes":[96,85],"count":2},{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2},{"pattern":"JUMPDEST PUSH1 PUSH1","bytes":[91,96,96],"count":2},{"pattern":"SSTORE JUMPDEST PUSH1 PUSH1","bytes":[85,91,96,96],"count":2},{"pattern":"PUSH1 SSTORE JUMPDEST PUSH1 PUSH1","bytes":[96,85,91,96,96],"count":2},{"pattern":"JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1 PUSH1","bytes":[91,96,85,91,96,96],"count":2},{"pattern":"JUMPDEST PUSH1 SSTORE","bytes":[91,96,85],"count":2},{"pattern":"PUSH1 SLOAD","bytes":[96,84],"count":2},{"pattern":"PUSH1 JUMPDEST","bytes":[96,91],"count":1},{"pattern":"PUSH1 JUMPDEST PUSH1 SSTORE","bytes":[96,91,96,85],"count":1},{"pattern":"PUSH1 JUMPDEST PUSH1","bytes":[96,91,96],"count":1},{"pattern":"PUSH1 JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1","bytes":[96,91,96,85,91,96],"count":1},{"pattern":"PUSH1 JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1 PUSH1","bytes":[96,91,96,85,91,96,96],"count":1},{"pattern":"PUSH1 JUMPDEST PUSH1 SSTORE JUMPDEST","bytes":[96,91,96,85,91],"count":1},{"pattern":"JUMPI JUMPDEST","bytes":[87,91],"count":1},{"pattern":"SUB DUP1 PUSH1 JUMPI JUMPDEST","bytes":[3,128,96,87,91],"count":1},{"pattern":"PUSH1 JUMPI JUMPDEST PUSH1","bytes":[96,87,91,96],"count":1},{"pattern":"JUMPI JUMPDEST PUSH1 SSTORE","bytes":[87,91,96,85],"count":1},{"pattern":"JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST","bytes":[87,91,96,85,91],"count":1},{"pattern":"PUSH1 JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1","bytes":[96,87,91,96,85,91,96],"count":1},{"pattern":"DUP1 PUSH1 JUMPI PUSH2","bytes":[128,96,87,97],"count":1},{"pattern":"PUSH2 POP","bytes":[97,80],"count":1},{"pattern":"SUB DUP1 PUSH1 JUMPI PUSH2 POP","bytes":[3,128,96,87,97,80],"count":1},{"pattern":"PUSH1 JUMPI JUMPDEST","bytes":[96,87,91],"count":1},{"pattern":"DUP1 PUSH1 JUMPI JUMPDEST","bytes":[128,96,87,91],"count":1},{"pattern":"SLOAD SUB DUP1 PUSH1 JUMPI JUMPDEST","bytes":[84,3,128,96,87,91],"count":1},{"pattern":"PUSH1 SLOAD SUB DUP1 PUSH1 JUMPI JUMPDEST","bytes":[96,84,3,128,96,87,91],"count":1},{"pattern":"JUMPI JUMPDEST PUSH1","bytes":[87,91,96],"count":1},{"pattern":"DUP1 PUSH1 JUMPI JUMPDEST PUSH1","bytes":[128,96,87,91,96],"count":1},{"pattern":"SUB DUP1 PUSH1 JUMPI JUMPDEST PUSH1","bytes":[3,128,96,87,91,96],"count":1},{"pattern":"SLOAD SUB DUP1 PUSH1 JUMPI JUMPDEST PUSH1","bytes":[84,3,128,96,87,91,96],"count":1},{"pattern":"PUSH1 JUMPI JUMPDEST PUSH1 SSTORE","bytes":[96,87,91,96,85],"count":1},{"pattern":"DUP1 PUSH1 JUMPI JUMPDEST PUSH1 SSTORE","bytes":[128,96,87,91,96,85],"count":1},{"pattern":"SUB DUP1 PUSH1 JUMPI JUMPDEST PUSH1 SSTORE","bytes":[3,128,96,87,91,96,85],"count":1},{"pattern":"PUSH1 JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST","bytes":[96,87,91,96,85,91],"count":1},{"pattern":"DUP1 PUSH1 JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST","bytes":[128,96,87,91,96,85,91],"count":1},{"pattern":"JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1","bytes":[87,91,96,85,91,96],"count":1},{"pattern":"JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1 PUSH1","bytes":[87,91,96,85,91,96,96],"count":1},{"pattern":"JUMPI PUSH2","bytes":[87,97],"count":1},{"pattern":"PUSH1 JUMPI PUSH2","bytes":[96,87,97],"count":1},{"pattern":"SUB DUP1 PUSH1 JUMPI PUSH2","bytes":[3,128,96,87,97],"count":1},{"pattern":"SLOAD SUB DUP1 PUSH1 JUMPI PUSH2","bytes":[84,3,128,96,87,97],"count":1},{"pattern":"PUSH1 SLOAD SUB DUP1 PUSH1 JUMPI PUSH2","bytes":[96,84,3,128,96,87,97],"count":1},{"pattern":"JUMPI PUSH2 POP","bytes":[87,97,80],"count":1},{"pattern":"PUSH1 JUMPI PUSH2 POP","bytes":[96,87,97,80],"count":1},{"pattern":"DUP1 PUSH1 JUMPI PUSH2 POP","bytes":[128,96,87,97,80],"count":1},{"pattern":"SLOAD SUB DUP1 PUSH1 JUMPI PUSH2 POP","bytes":[84,3,128,96,87,97,80],"count":1}]}"""
        );
        yield return new TestCaseData(
            new[]
            {
                //                   N=5     --                                  pattern appears N times                                                   --      --  Pattern Appears once    --
                Prepare.EvmCode.PushData(5).JUMPDEST().PushData(1).SSTORE().JUMPDEST().PushData(1).PushData(1).SLOAD()
                    .SUB().DUPx(1).PushData(2).JUMPI().PushData(23).PushData(7).ADD().POP().STOP().Done
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.678,"confidence":0.9375,"stats":[{"pattern":"JUMPDEST PUSH1","bytes":[91,96],"count":10},{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":6},{"pattern":"SSTORE JUMPDEST","bytes":[85,91],"count":5},{"pattern":"PUSH1 SSTORE JUMPDEST","bytes":[96,85,91],"count":5},{"pattern":"JUMPDEST PUSH1 SSTORE JUMPDEST","bytes":[91,96,85,91],"count":5},{"pattern":"SSTORE JUMPDEST PUSH1","bytes":[85,91,96],"count":5},{"pattern":"PUSH1 SSTORE JUMPDEST PUSH1","bytes":[96,85,91,96],"count":5},{"pattern":"PUSH1 SSTORE JUMPDEST PUSH1 PUSH1 SLOAD","bytes":[96,85,91,96,96,84],"count":5},{"pattern":"JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1 PUSH1 SLOAD","bytes":[91,96,85,91,96,96,84],"count":5},{"pattern":"SLOAD SUB","bytes":[84,3],"count":5},{"pattern":"PUSH1 SLOAD SUB","bytes":[96,84,3],"count":5},{"pattern":"PUSH1 PUSH1 SLOAD SUB","bytes":[96,96,84,3],"count":5},{"pattern":"JUMPDEST PUSH1 PUSH1 SLOAD SUB","bytes":[91,96,96,84,3],"count":5},{"pattern":"SSTORE JUMPDEST PUSH1 PUSH1 SLOAD SUB","bytes":[85,91,96,96,84,3],"count":5},{"pattern":"PUSH1 SSTORE JUMPDEST PUSH1 PUSH1 SLOAD SUB","bytes":[96,85,91,96,96,84,3],"count":5},{"pattern":"SUB DUP1","bytes":[3,128],"count":5},{"pattern":"SLOAD SUB DUP1","bytes":[84,3,128],"count":5},{"pattern":"PUSH1 SLOAD SUB DUP1","bytes":[96,84,3,128],"count":5},{"pattern":"PUSH1 PUSH1 SLOAD SUB DUP1","bytes":[96,96,84,3,128],"count":5},{"pattern":"JUMPDEST PUSH1 PUSH1 SLOAD SUB DUP1","bytes":[91,96,96,84,3,128],"count":5},{"pattern":"SSTORE JUMPDEST PUSH1 PUSH1 SLOAD SUB DUP1","bytes":[85,91,96,96,84,3,128],"count":5},{"pattern":"DUP1 PUSH1","bytes":[128,96],"count":5},{"pattern":"SUB DUP1 PUSH1","bytes":[3,128,96],"count":5},{"pattern":"SLOAD SUB DUP1 PUSH1","bytes":[84,3,128,96],"count":5},{"pattern":"PUSH1 SLOAD SUB DUP1 PUSH1","bytes":[96,84,3,128,96],"count":5},{"pattern":"PUSH1 PUSH1 SLOAD SUB DUP1 PUSH1","bytes":[96,96,84,3,128,96],"count":5},{"pattern":"JUMPDEST PUSH1 PUSH1 SLOAD SUB DUP1 PUSH1","bytes":[91,96,96,84,3,128,96],"count":5},{"pattern":"PUSH1 JUMPI","bytes":[96,87],"count":5},{"pattern":"DUP1 PUSH1 JUMPI","bytes":[128,96,87],"count":5},{"pattern":"SUB DUP1 PUSH1 JUMPI","bytes":[3,128,96,87],"count":5},{"pattern":"SLOAD SUB DUP1 PUSH1 JUMPI","bytes":[84,3,128,96,87],"count":5},{"pattern":"PUSH1 SLOAD SUB DUP1 PUSH1 JUMPI","bytes":[96,84,3,128,96,87],"count":5},{"pattern":"PUSH1 PUSH1 SLOAD SUB DUP1 PUSH1 JUMPI","bytes":[96,96,84,3,128,96,87],"count":5},{"pattern":"JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1","bytes":[91,96,85,91,96],"count":5},{"pattern":"PUSH1 SSTORE","bytes":[96,85],"count":5},{"pattern":"JUMPDEST PUSH1 PUSH1","bytes":[91,96,96],"count":5},{"pattern":"SSTORE JUMPDEST PUSH1 PUSH1","bytes":[85,91,96,96],"count":5},{"pattern":"PUSH1 SSTORE JUMPDEST PUSH1 PUSH1","bytes":[96,85,91,96,96],"count":5},{"pattern":"JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1 PUSH1","bytes":[91,96,85,91,96,96],"count":5},{"pattern":"JUMPDEST PUSH1 SSTORE","bytes":[91,96,85],"count":5},{"pattern":"PUSH1 SLOAD","bytes":[96,84],"count":5},{"pattern":"PUSH1 PUSH1 SLOAD","bytes":[96,96,84],"count":5},{"pattern":"JUMPDEST PUSH1 PUSH1 SLOAD","bytes":[91,96,96,84],"count":5},{"pattern":"SSTORE JUMPDEST PUSH1 PUSH1 SLOAD","bytes":[85,91,96,96,84],"count":5},{"pattern":"JUMPI JUMPDEST","bytes":[87,91],"count":4},{"pattern":"SUB DUP1 PUSH1 JUMPI JUMPDEST","bytes":[3,128,96,87,91],"count":4},{"pattern":"PUSH1 JUMPI JUMPDEST PUSH1","bytes":[96,87,91,96],"count":4},{"pattern":"JUMPI JUMPDEST PUSH1 SSTORE","bytes":[87,91,96,85],"count":4},{"pattern":"JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST","bytes":[87,91,96,85,91],"count":4},{"pattern":"PUSH1 JUMPI JUMPDEST","bytes":[96,87,91],"count":4},{"pattern":"DUP1 PUSH1 JUMPI JUMPDEST","bytes":[128,96,87,91],"count":4},{"pattern":"SLOAD SUB DUP1 PUSH1 JUMPI JUMPDEST","bytes":[84,3,128,96,87,91],"count":4},{"pattern":"PUSH1 SLOAD SUB DUP1 PUSH1 JUMPI JUMPDEST","bytes":[96,84,3,128,96,87,91],"count":4},{"pattern":"JUMPI JUMPDEST PUSH1","bytes":[87,91,96],"count":4},{"pattern":"DUP1 PUSH1 JUMPI JUMPDEST PUSH1","bytes":[128,96,87,91,96],"count":4},{"pattern":"SUB DUP1 PUSH1 JUMPI JUMPDEST PUSH1","bytes":[3,128,96,87,91,96],"count":4},{"pattern":"SLOAD SUB DUP1 PUSH1 JUMPI JUMPDEST PUSH1","bytes":[84,3,128,96,87,91,96],"count":4},{"pattern":"PUSH1 JUMPI JUMPDEST PUSH1 SSTORE","bytes":[96,87,91,96,85],"count":4},{"pattern":"DUP1 PUSH1 JUMPI JUMPDEST PUSH1 SSTORE","bytes":[128,96,87,91,96,85],"count":4},{"pattern":"SUB DUP1 PUSH1 JUMPI JUMPDEST PUSH1 SSTORE","bytes":[3,128,96,87,91,96,85],"count":4},{"pattern":"PUSH1 JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST","bytes":[96,87,91,96,85,91],"count":4},{"pattern":"DUP1 PUSH1 JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST","bytes":[128,96,87,91,96,85,91],"count":4},{"pattern":"JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1","bytes":[87,91,96,85,91,96],"count":4},{"pattern":"JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1 PUSH1","bytes":[87,91,96,85,91,96,96],"count":4},{"pattern":"PUSH1 JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1","bytes":[96,87,91,96,85,91,96],"count":4},{"pattern":"PUSH1 JUMPDEST","bytes":[96,91],"count":1},{"pattern":"PUSH1 JUMPDEST PUSH1 SSTORE","bytes":[96,91,96,85],"count":1},{"pattern":"PUSH1 JUMPDEST PUSH1","bytes":[96,91,96],"count":1},{"pattern":"PUSH1 JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1","bytes":[96,91,96,85,91,96],"count":1},{"pattern":"PUSH1 JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1 PUSH1","bytes":[96,91,96,85,91,96,96],"count":1},{"pattern":"PUSH1 JUMPI PUSH1 PUSH1 ADD","bytes":[96,87,96,96,1],"count":1},{"pattern":"PUSH1 JUMPDEST PUSH1 SSTORE JUMPDEST","bytes":[96,91,96,85,91],"count":1},{"pattern":"JUMPI PUSH1","bytes":[87,96],"count":1},{"pattern":"DUP1 PUSH1 JUMPI PUSH1","bytes":[128,96,87,96],"count":1},{"pattern":"JUMPI PUSH1 PUSH1","bytes":[87,96,96],"count":1},{"pattern":"SLOAD SUB DUP1 PUSH1 JUMPI PUSH1 PUSH1","bytes":[84,3,128,96,87,96,96],"count":1},{"pattern":"DUP1 PUSH1 JUMPI PUSH1 PUSH1 ADD","bytes":[128,96,87,96,96,1],"count":1},{"pattern":"PUSH1 ADD POP","bytes":[96,1,80],"count":1},{"pattern":"DUP1 PUSH1 JUMPI PUSH1 PUSH1 ADD POP","bytes":[128,96,87,96,96,1,80],"count":1},{"pattern":"PUSH1 JUMPI PUSH1","bytes":[96,87,96],"count":1},{"pattern":"SUB DUP1 PUSH1 JUMPI PUSH1","bytes":[3,128,96,87,96],"count":1},{"pattern":"SLOAD SUB DUP1 PUSH1 JUMPI PUSH1","bytes":[84,3,128,96,87,96],"count":1},{"pattern":"PUSH1 SLOAD SUB DUP1 PUSH1 JUMPI PUSH1","bytes":[96,84,3,128,96,87,96],"count":1},{"pattern":"PUSH1 JUMPI PUSH1 PUSH1","bytes":[96,87,96,96],"count":1},{"pattern":"DUP1 PUSH1 JUMPI PUSH1 PUSH1","bytes":[128,96,87,96,96],"count":1},{"pattern":"SUB DUP1 PUSH1 JUMPI PUSH1 PUSH1","bytes":[3,128,96,87,96,96],"count":1},{"pattern":"PUSH1 ADD","bytes":[96,1],"count":1},{"pattern":"PUSH1 PUSH1 ADD","bytes":[96,96,1],"count":1},{"pattern":"JUMPI PUSH1 PUSH1 ADD","bytes":[87,96,96,1],"count":1},{"pattern":"SUB DUP1 PUSH1 JUMPI PUSH1 PUSH1 ADD","bytes":[3,128,96,87,96,96,1],"count":1},{"pattern":"ADD POP","bytes":[1,80],"count":1},{"pattern":"PUSH1 PUSH1 ADD POP","bytes":[96,96,1,80],"count":1},{"pattern":"JUMPI PUSH1 PUSH1 ADD POP","bytes":[87,96,96,1,80],"count":1},{"pattern":"PUSH1 JUMPI PUSH1 PUSH1 ADD POP","bytes":[96,87,96,96,1,80],"count":1}]}"""
        );
        yield return new TestCaseData(
            new[]
            {
                Prepare.EvmCode.PushData(10).JUMPDEST().PushData(1).SSTORE().JUMPDEST().PushData(1).PushData(1).SLOAD()
                    .SUB().DUPx(1).PushData(2).JUMPI().PushData(23).PushData(7).ADD().POP().STOP().Done
            }, // error doubles as N doubles
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":1.338,"confidence":0.9375,"stats":[{"pattern":"JUMPDEST PUSH1","bytes":[91,96],"count":20},{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":11},{"pattern":"SSTORE JUMPDEST","bytes":[85,91],"count":10},{"pattern":"PUSH1 SSTORE JUMPDEST","bytes":[96,85,91],"count":10},{"pattern":"JUMPDEST PUSH1 SSTORE JUMPDEST","bytes":[91,96,85,91],"count":10},{"pattern":"SSTORE JUMPDEST PUSH1","bytes":[85,91,96],"count":10},{"pattern":"PUSH1 SSTORE JUMPDEST PUSH1","bytes":[96,85,91,96],"count":10},{"pattern":"PUSH1 SSTORE JUMPDEST PUSH1 PUSH1 SLOAD","bytes":[96,85,91,96,96,84],"count":10},{"pattern":"JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1 PUSH1 SLOAD","bytes":[91,96,85,91,96,96,84],"count":10},{"pattern":"SLOAD SUB","bytes":[84,3],"count":10},{"pattern":"PUSH1 SLOAD SUB","bytes":[96,84,3],"count":10},{"pattern":"PUSH1 PUSH1 SLOAD SUB","bytes":[96,96,84,3],"count":10},{"pattern":"JUMPDEST PUSH1 PUSH1 SLOAD SUB","bytes":[91,96,96,84,3],"count":10},{"pattern":"SSTORE JUMPDEST PUSH1 PUSH1 SLOAD SUB","bytes":[85,91,96,96,84,3],"count":10},{"pattern":"PUSH1 SSTORE JUMPDEST PUSH1 PUSH1 SLOAD SUB","bytes":[96,85,91,96,96,84,3],"count":10},{"pattern":"SUB DUP1","bytes":[3,128],"count":10},{"pattern":"SLOAD SUB DUP1","bytes":[84,3,128],"count":10},{"pattern":"PUSH1 SLOAD SUB DUP1","bytes":[96,84,3,128],"count":10},{"pattern":"PUSH1 PUSH1 SLOAD SUB DUP1","bytes":[96,96,84,3,128],"count":10},{"pattern":"JUMPDEST PUSH1 PUSH1 SLOAD SUB DUP1","bytes":[91,96,96,84,3,128],"count":10},{"pattern":"SSTORE JUMPDEST PUSH1 PUSH1 SLOAD SUB DUP1","bytes":[85,91,96,96,84,3,128],"count":10},{"pattern":"DUP1 PUSH1","bytes":[128,96],"count":10},{"pattern":"SUB DUP1 PUSH1","bytes":[3,128,96],"count":10},{"pattern":"SLOAD SUB DUP1 PUSH1","bytes":[84,3,128,96],"count":10},{"pattern":"PUSH1 SLOAD SUB DUP1 PUSH1","bytes":[96,84,3,128,96],"count":10},{"pattern":"PUSH1 PUSH1 SLOAD SUB DUP1 PUSH1","bytes":[96,96,84,3,128,96],"count":10},{"pattern":"JUMPDEST PUSH1 PUSH1 SLOAD SUB DUP1 PUSH1","bytes":[91,96,96,84,3,128,96],"count":10},{"pattern":"PUSH1 JUMPI","bytes":[96,87],"count":10},{"pattern":"DUP1 PUSH1 JUMPI","bytes":[128,96,87],"count":10},{"pattern":"SUB DUP1 PUSH1 JUMPI","bytes":[3,128,96,87],"count":10},{"pattern":"SLOAD SUB DUP1 PUSH1 JUMPI","bytes":[84,3,128,96,87],"count":10},{"pattern":"PUSH1 SLOAD SUB DUP1 PUSH1 JUMPI","bytes":[96,84,3,128,96,87],"count":10},{"pattern":"PUSH1 PUSH1 SLOAD SUB DUP1 PUSH1 JUMPI","bytes":[96,96,84,3,128,96,87],"count":10},{"pattern":"JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1","bytes":[91,96,85,91,96],"count":10},{"pattern":"PUSH1 SSTORE","bytes":[96,85],"count":10},{"pattern":"JUMPDEST PUSH1 PUSH1","bytes":[91,96,96],"count":10},{"pattern":"SSTORE JUMPDEST PUSH1 PUSH1","bytes":[85,91,96,96],"count":10},{"pattern":"PUSH1 SSTORE JUMPDEST PUSH1 PUSH1","bytes":[96,85,91,96,96],"count":10},{"pattern":"JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1 PUSH1","bytes":[91,96,85,91,96,96],"count":10},{"pattern":"JUMPDEST PUSH1 SSTORE","bytes":[91,96,85],"count":10},{"pattern":"PUSH1 SLOAD","bytes":[96,84],"count":10},{"pattern":"PUSH1 PUSH1 SLOAD","bytes":[96,96,84],"count":10},{"pattern":"JUMPDEST PUSH1 PUSH1 SLOAD","bytes":[91,96,96,84],"count":10},{"pattern":"SSTORE JUMPDEST PUSH1 PUSH1 SLOAD","bytes":[85,91,96,96,84],"count":10},{"pattern":"JUMPI JUMPDEST","bytes":[87,91],"count":9},{"pattern":"SUB DUP1 PUSH1 JUMPI JUMPDEST","bytes":[3,128,96,87,91],"count":9},{"pattern":"PUSH1 JUMPI JUMPDEST PUSH1","bytes":[96,87,91,96],"count":9},{"pattern":"JUMPI JUMPDEST PUSH1 SSTORE","bytes":[87,91,96,85],"count":9},{"pattern":"JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST","bytes":[87,91,96,85,91],"count":9},{"pattern":"PUSH1 JUMPI JUMPDEST","bytes":[96,87,91],"count":9},{"pattern":"DUP1 PUSH1 JUMPI JUMPDEST","bytes":[128,96,87,91],"count":9},{"pattern":"SLOAD SUB DUP1 PUSH1 JUMPI JUMPDEST","bytes":[84,3,128,96,87,91],"count":9},{"pattern":"PUSH1 SLOAD SUB DUP1 PUSH1 JUMPI JUMPDEST","bytes":[96,84,3,128,96,87,91],"count":9},{"pattern":"JUMPI JUMPDEST PUSH1","bytes":[87,91,96],"count":9},{"pattern":"DUP1 PUSH1 JUMPI JUMPDEST PUSH1","bytes":[128,96,87,91,96],"count":9},{"pattern":"SUB DUP1 PUSH1 JUMPI JUMPDEST PUSH1","bytes":[3,128,96,87,91,96],"count":9},{"pattern":"SLOAD SUB DUP1 PUSH1 JUMPI JUMPDEST PUSH1","bytes":[84,3,128,96,87,91,96],"count":9},{"pattern":"PUSH1 JUMPI JUMPDEST PUSH1 SSTORE","bytes":[96,87,91,96,85],"count":9},{"pattern":"DUP1 PUSH1 JUMPI JUMPDEST PUSH1 SSTORE","bytes":[128,96,87,91,96,85],"count":9},{"pattern":"SUB DUP1 PUSH1 JUMPI JUMPDEST PUSH1 SSTORE","bytes":[3,128,96,87,91,96,85],"count":9},{"pattern":"PUSH1 JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST","bytes":[96,87,91,96,85,91],"count":9},{"pattern":"DUP1 PUSH1 JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST","bytes":[128,96,87,91,96,85,91],"count":9},{"pattern":"JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1","bytes":[87,91,96,85,91,96],"count":9},{"pattern":"JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1 PUSH1","bytes":[87,91,96,85,91,96,96],"count":9},{"pattern":"PUSH1 JUMPI JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1","bytes":[96,87,91,96,85,91,96],"count":9},{"pattern":"PUSH1 JUMPDEST","bytes":[96,91],"count":1},{"pattern":"PUSH1 JUMPDEST PUSH1 SSTORE","bytes":[96,91,96,85],"count":1},{"pattern":"PUSH1 JUMPDEST PUSH1","bytes":[96,91,96],"count":1},{"pattern":"PUSH1 JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1","bytes":[96,91,96,85,91,96],"count":1},{"pattern":"PUSH1 JUMPDEST PUSH1 SSTORE JUMPDEST PUSH1 PUSH1","bytes":[96,91,96,85,91,96,96],"count":1},{"pattern":"PUSH1 JUMPI PUSH1 PUSH1 ADD","bytes":[96,87,96,96,1],"count":1},{"pattern":"PUSH1 JUMPDEST PUSH1 SSTORE JUMPDEST","bytes":[96,91,96,85,91],"count":1},{"pattern":"JUMPI PUSH1","bytes":[87,96],"count":1},{"pattern":"DUP1 PUSH1 JUMPI PUSH1","bytes":[128,96,87,96],"count":1},{"pattern":"JUMPI PUSH1 PUSH1","bytes":[87,96,96],"count":1},{"pattern":"SLOAD SUB DUP1 PUSH1 JUMPI PUSH1 PUSH1","bytes":[84,3,128,96,87,96,96],"count":1},{"pattern":"DUP1 PUSH1 JUMPI PUSH1 PUSH1 ADD","bytes":[128,96,87,96,96,1],"count":1},{"pattern":"PUSH1 ADD POP","bytes":[96,1,80],"count":1},{"pattern":"DUP1 PUSH1 JUMPI PUSH1 PUSH1 ADD POP","bytes":[128,96,87,96,96,1,80],"count":1},{"pattern":"PUSH1 JUMPI PUSH1","bytes":[96,87,96],"count":1},{"pattern":"SUB DUP1 PUSH1 JUMPI PUSH1","bytes":[3,128,96,87,96],"count":1},{"pattern":"SLOAD SUB DUP1 PUSH1 JUMPI PUSH1","bytes":[84,3,128,96,87,96],"count":1},{"pattern":"PUSH1 SLOAD SUB DUP1 PUSH1 JUMPI PUSH1","bytes":[96,84,3,128,96,87,96],"count":1},{"pattern":"PUSH1 JUMPI PUSH1 PUSH1","bytes":[96,87,96,96],"count":1},{"pattern":"DUP1 PUSH1 JUMPI PUSH1 PUSH1","bytes":[128,96,87,96,96],"count":1},{"pattern":"SUB DUP1 PUSH1 JUMPI PUSH1 PUSH1","bytes":[3,128,96,87,96,96],"count":1},{"pattern":"PUSH1 ADD","bytes":[96,1],"count":1},{"pattern":"PUSH1 PUSH1 ADD","bytes":[96,96,1],"count":1},{"pattern":"JUMPI PUSH1 PUSH1 ADD","bytes":[87,96,96,1],"count":1},{"pattern":"SUB DUP1 PUSH1 JUMPI PUSH1 PUSH1 ADD","bytes":[3,128,96,87,96,96,1],"count":1},{"pattern":"ADD POP","bytes":[1,80],"count":1},{"pattern":"PUSH1 PUSH1 ADD POP","bytes":[96,96,1,80],"count":1},{"pattern":"JUMPI PUSH1 PUSH1 ADD POP","bytes":[87,96,96,1,80],"count":1},{"pattern":"PUSH1 JUMPI PUSH1 PUSH1 ADD POP","bytes":[96,87,96,96,1,80],"count":1}]}"""
        );

    }
}
