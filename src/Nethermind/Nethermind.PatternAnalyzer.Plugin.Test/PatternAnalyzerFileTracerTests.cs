
// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Evm.Test;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.State;
using System.Linq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using Nethermind.PatternAnalyzer.Plugin;
using System.IO.Abstractions.TestingHelpers;
using Nethermind.PatternAnalyzer.Plugin.Analyzer;
using Nethermind.PatternAnalyzer.Plugin.Tracer;
using System.Threading.Tasks;
using Nethermind.Evm;
using Nethermind.Logging;
using System.Threading;
using System.Text;
using Nethermind.Evm.Tracing;
using Nethermind.Core.Specs;

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
    private HashSet<Instruction> _ignoreSet = new HashSet<Instruction> {Instruction.JUMPDEST, Instruction.JUMP};

    public override void Setup()

    {
         base.Setup();
        _fileSystem = new MockFileSystem();
        ILogManager _logManager = LimboLogs.Instance;
        var mockFileData = new MockFileData("File content");
        _testFileName =  _fileSystem.Path.Combine(".","test_opcode_stats.json");
        _testIgnoreFileName =  _fileSystem.Path.Combine(".","test_opcode_stats_ignore.json");
        _fileSystem.AddDirectory(_fileSystem.Path.GetTempPath());
        _fileSystem.AddFile(_testFileName, mockFileData );
        _fileSystem.AddFile(_testIgnoreFileName, mockFileData );
        _logger = _logManager.GetClassLogger();

        CMSketch sketch = new CMSketchBuilder().SetBuckets(1000).SetHashFunctions(4).Build();
        _statsAnalyzer = new StatsAnalyzerBuilder().SetBufferSizeForSketches(2).SetTopN(100).SetCapacity(100000)
                                      .SetMinSupport(1).SetSketchResetOrReuseThreshold(0.001).SetSketch(sketch).Build();

        CMSketch sketch2 = new CMSketchBuilder().SetBuckets(1000).SetHashFunctions(4).Build();
        _statsAnalyzerIgnore = new StatsAnalyzerBuilder().SetBufferSizeForSketches(2).SetTopN(100).SetCapacity(100000)
                                      .SetMinSupport(1).SetSketchResetOrReuseThreshold(0.001).SetSketch(sketch2).Build();

       _tracer = new PatternAnalyzerFileTracer(1, 100, _statsAnalyzer, new HashSet<Instruction>(), _fileSystem, _logger, 1, _testFileName);
       _tracerIgnore = new PatternAnalyzerFileTracer(1, 100, _statsAnalyzerIgnore, _ignoreSet, _fileSystem, _logger, 1, _testIgnoreFileName);
    }


    void ExecuteTransactions(byte[][] codes, PatternAnalyzerFileTracer tracer)
    {
      ForkActivation forkActivation = MainnetSpecProvider.PragueActivation;
        (Block block, _) = PrepareTx(forkActivation , 100000, codes[0]);
        tracer.StartNewBlockTrace(block);
        foreach (var  code in codes)
        {
            (_, Transaction transaction) = PrepareTx(forkActivation , 100000, code);
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
        Assert.That(fileContent, Is.EqualTo(expectedTrace));
    }

    [TestCaseSource(nameof(GetIgnoreSetCases))]
    public void Test_File_Tracer_With_Ignore_Set(byte[][] codes, string expectedTrace)
    {

        ExecuteTransactions(codes, _tracerIgnore);
        var fileContent = _fileSystem.File.ReadAllText(_testIgnoreFileName);
        Assert.That(fileContent, Is.Not.Empty); //IsNotEmpty(fileContent);
        Assert.That(fileContent, Is.EqualTo(expectedTrace));
    }

    private static IEnumerable<TestCaseData> GetIgnoreSetCases()
    {
        yield return new TestCaseData(
            new byte[][] {
             Prepare.EvmCode.JUMPDEST().PushData(6).JUMP().JUMPDEST().JUMPDEST().JUMPDEST().JUMPDEST().JUMPDEST().PushData(0).PushData(0).STOP().Done,
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.006,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":1}]}"""
        );
        yield return new TestCaseData(
            new byte[][] {
             Prepare.EvmCode.JUMPDEST().PushData(6).JUMP().PushData(6).JUMPDEST().PushData(0).PushData(0).STOP().Done,
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.006,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":1}]}"""
        );
        yield return new TestCaseData(
            new byte[][] {
             Prepare.EvmCode.JUMPDEST().PushData(0).JUMPDEST().PushData(6).JUMPDEST().PushData(0).Done,
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.006,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":1}]}"""
        );
        yield return new TestCaseData(
            new byte[][] {
             Prepare.EvmCode.PushData(0).PushData(6).JUMPDEST().PushData(0).Done,
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.006,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":1}]}"""
        );


    }

    private static IEnumerable<TestCaseData> GetTraceCases()
    {
        yield return new TestCaseData(
            new byte[][] {
             Prepare.EvmCode.PushData(0).PushData(0).PushData(0).Done,
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.006,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":1}]}"""
        );
        yield return new TestCaseData(
            new byte[][] {
             Prepare.EvmCode.PushData(0).PushData(0).PushData(0).Done,
             Prepare.EvmCode.PushData(0).Done,
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.006,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":1}]}"""
        );
        yield return new TestCaseData(
            new byte[][] {
             Prepare.EvmCode.PushData(0).PushData(0).PushData(0).Done,
             Prepare.EvmCode.PushData(0).PushData(0).PushData(0).Done,
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.012,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":4},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":2}]}"""
        );
        yield return new TestCaseData(
            new byte[][] {
             Prepare.EvmCode.PushData(0).PushData(0).PushData(0).PushData(0).PushData(0).PushData(0).Done,
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.03,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":5},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":4},{"pattern":"PUSH1 PUSH1 PUSH1 PUSH1","bytes":[96,96,96,96],"count":3},{"pattern":"PUSH1 PUSH1 PUSH1 PUSH1 PUSH1","bytes":[96,96,96,96,96],"count":2},{"pattern":"PUSH1 PUSH1 PUSH1 PUSH1 PUSH1 PUSH1","bytes":[96,96,96,96,96,96],"count":1}]}"""
        );

    //    yield return new TestCaseData(
    //        new byte[][] {
    //            Prepare.EvmCode.PushData(5).PushData(1).SSTORE().JUMPDEST().PushData(1000).GAS().LT().PUSHx([0, 26]).JUMPI().PushData(23).PushData(7).ADD().POP().PushData(42).PushData(5).ADD().POP().PUSHx([0, 0]).JUMP().JUMPDEST().STOP().Done,
    //        },
    //        """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":272.49,"confidence":0.9375,"stats":[{"pattern":"PUSH1 ADD","bytes":[96,1],"count":2838},{"pattern":"PUSH1 PUSH1 ADD","bytes":[96,96,1],"count":2838},{"pattern":"ADD POP","bytes":[1,80],"count":2838},{"pattern":"PUSH1 ADD POP","bytes":[96,1,80],"count":2838},{"pattern":"PUSH1 PUSH1 ADD POP","bytes":[96,96,1,80],"count":2838},{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2838},{"pattern":"LT PUSH2","bytes":[16,97],"count":1420},{"pattern":"GAS LT PUSH2","bytes":[90,16,97],"count":1420},{"pattern":"PUSH2 GAS LT PUSH2","bytes":[97,90,16,97],"count":1420},{"pattern":"JUMPDEST PUSH2 GAS LT PUSH2","bytes":[91,97,90,16,97],"count":1420},{"pattern":"JUMPDEST PUSH2 GAS","bytes":[91,97,90],"count":1420},{"pattern":"PUSH2 JUMPI","bytes":[97,87],"count":1420},{"pattern":"LT PUSH2 JUMPI","bytes":[16,97,87],"count":1420},{"pattern":"GAS LT PUSH2 JUMPI","bytes":[90,16,97,87],"count":1420},{"pattern":"PUSH2 GAS LT PUSH2 JUMPI","bytes":[97,90,16,97,87],"count":1420},{"pattern":"JUMPDEST PUSH2 GAS LT PUSH2 JUMPI","bytes":[91,97,90,16,97,87],"count":1420},{"pattern":"GAS LT","bytes":[90,16],"count":1420},{"pattern":"JUMPDEST PUSH2","bytes":[91,97],"count":1420},{"pattern":"PUSH2 GAS LT","bytes":[97,90,16],"count":1420},{"pattern":"JUMPDEST PUSH2 GAS LT","bytes":[91,97,90,16],"count":1420},{"pattern":"PUSH2 GAS","bytes":[97,90],"count":1420},{"pattern":"PUSH2 JUMPI PUSH1 PUSH1 ADD POP","bytes":[97,87,96,96,1,80],"count":1419},{"pattern":"PUSH2 JUMPI PUSH1","bytes":[97,87,96],"count":1419},{"pattern":"LT PUSH2 JUMPI PUSH1","bytes":[16,97,87,96],"count":1419},{"pattern":"GAS LT PUSH2 JUMPI PUSH1 PUSH1","bytes":[90,16,97,87,96,96],"count":1419},{"pattern":"JUMPI PUSH1 PUSH1 ADD","bytes":[87,96,96,1],"count":1419},{"pattern":"JUMPI PUSH1 PUSH1 ADD POP","bytes":[87,96,96,1,80],"count":1419},{"pattern":"LT PUSH2 JUMPI PUSH1 PUSH1 ADD POP","bytes":[16,97,87,96,96,1,80],"count":1419},{"pattern":"PUSH1 ADD POP PUSH1","bytes":[96,1,80,96],"count":1419},{"pattern":"POP PUSH1 PUSH1","bytes":[80,96,96],"count":1419},{"pattern":"JUMPI PUSH1 PUSH1 ADD POP PUSH1 PUSH1","bytes":[87,96,96,1,80,96,96],"count":1419},{"pattern":"PUSH1 PUSH1 ADD POP PUSH1 PUSH1 ADD","bytes":[96,96,1,80,96,96,1],"count":1419},{"pattern":"POP PUSH2","bytes":[80,97],"count":1419},{"pattern":"POP PUSH1 PUSH1 ADD POP PUSH2","bytes":[80,96,96,1,80,97],"count":1419},{"pattern":"ADD POP PUSH2 JUMP","bytes":[1,80,97,86],"count":1419},{"pattern":"JUMP JUMPDEST","bytes":[86,91],"count":1419},{"pattern":"GAS LT PUSH2 JUMPI PUSH1","bytes":[90,16,97,87,96],"count":1419},{"pattern":"PUSH2 GAS LT PUSH2 JUMPI PUSH1","bytes":[97,90,16,97,87,96],"count":1419},{"pattern":"JUMPDEST PUSH2 GAS LT PUSH2 JUMPI PUSH1","bytes":[91,97,90,16,97,87,96],"count":1419},{"pattern":"PUSH2 JUMP JUMPDEST PUSH2 GAS LT","bytes":[97,86,91,97,90,16],"count":1419},{"pattern":"JUMPI PUSH1","bytes":[87,96],"count":1419},{"pattern":"LT PUSH2 JUMPI PUSH1 PUSH1","bytes":[16,97,87,96,96],"count":1419},{"pattern":"PUSH2 GAS LT PUSH2 JUMPI PUSH1 PUSH1","bytes":[97,90,16,97,87,96,96],"count":1419},{"pattern":"PUSH2 JUMPI PUSH1 PUSH1 ADD","bytes":[97,87,96,96,1],"count":1419},{"pattern":"LT PUSH2 JUMPI PUSH1 PUSH1 ADD","bytes":[16,97,87,96,96,1],"count":1419},{"pattern":"GAS LT PUSH2 JUMPI PUSH1 PUSH1 ADD","bytes":[90,16,97,87,96,96,1],"count":1419},{"pattern":"POP PUSH1","bytes":[80,96],"count":1419},{"pattern":"ADD POP PUSH1","bytes":[1,80,96],"count":1419},{"pattern":"PUSH1 PUSH1 ADD POP PUSH1","bytes":[96,96,1,80,96],"count":1419},{"pattern":"JUMPI PUSH1 PUSH1 ADD POP PUSH1","bytes":[87,96,96,1,80,96],"count":1419},{"pattern":"PUSH2 JUMPI PUSH1 PUSH1 ADD POP PUSH1","bytes":[97,87,96,96,1,80,96],"count":1419},{"pattern":"ADD POP PUSH1 PUSH1","bytes":[1,80,96,96],"count":1419},{"pattern":"PUSH1 ADD POP PUSH1 PUSH1","bytes":[96,1,80,96,96],"count":1419},{"pattern":"PUSH1 PUSH1 ADD POP PUSH1 PUSH1","bytes":[96,96,1,80,96,96],"count":1419},{"pattern":"POP PUSH1 PUSH1 ADD","bytes":[80,96,96,1],"count":1419},{"pattern":"ADD POP PUSH1 PUSH1 ADD","bytes":[1,80,96,96,1],"count":1419},{"pattern":"PUSH1 ADD POP PUSH1 PUSH1 ADD","bytes":[96,1,80,96,96,1],"count":1419},{"pattern":"POP PUSH1 PUSH1 ADD POP","bytes":[80,96,96,1,80],"count":1419},{"pattern":"ADD POP PUSH1 PUSH1 ADD POP","bytes":[1,80,96,96,1,80],"count":1419},{"pattern":"PUSH1 ADD POP PUSH1 PUSH1 ADD POP","bytes":[96,1,80,96,96,1,80],"count":1419},{"pattern":"ADD POP PUSH2","bytes":[1,80,97],"count":1419},{"pattern":"PUSH1 ADD POP PUSH2","bytes":[96,1,80,97],"count":1419},{"pattern":"PUSH1 PUSH1 ADD POP PUSH2","bytes":[96,96,1,80,97],"count":1419},{"pattern":"ADD POP PUSH1 PUSH1 ADD POP PUSH2","bytes":[1,80,96,96,1,80,97],"count":1419},{"pattern":"PUSH2 JUMP","bytes":[97,86],"count":1419},{"pattern":"POP PUSH2 JUMP","bytes":[80,97,86],"count":1419},{"pattern":"PUSH1 ADD POP PUSH2 JUMP","bytes":[96,1,80,97,86],"count":1419},{"pattern":"PUSH1 PUSH1 ADD POP PUSH2 JUMP","bytes":[96,96,1,80,97,86],"count":1419},{"pattern":"POP PUSH1 PUSH1 ADD POP PUSH2 JUMP","bytes":[80,96,96,1,80,97,86],"count":1419},{"pattern":"PUSH2 JUMP JUMPDEST","bytes":[97,86,91],"count":1419},{"pattern":"POP PUSH2 JUMP JUMPDEST","bytes":[80,97,86,91],"count":1419},{"pattern":"ADD POP PUSH2 JUMP JUMPDEST","bytes":[1,80,97,86,91],"count":1419},{"pattern":"PUSH1 ADD POP PUSH2 JUMP JUMPDEST","bytes":[96,1,80,97,86,91],"count":1419},{"pattern":"PUSH1 PUSH1 ADD POP PUSH2 JUMP JUMPDEST","bytes":[96,96,1,80,97,86,91],"count":1419},{"pattern":"JUMP JUMPDEST PUSH2","bytes":[86,91,97],"count":1419},{"pattern":"PUSH2 JUMP JUMPDEST PUSH2","bytes":[97,86,91,97],"count":1419},{"pattern":"POP PUSH2 JUMP JUMPDEST PUSH2","bytes":[80,97,86,91,97],"count":1419},{"pattern":"ADD POP PUSH2 JUMP JUMPDEST PUSH2","bytes":[1,80,97,86,91,97],"count":1419},{"pattern":"PUSH1 ADD POP PUSH2 JUMP JUMPDEST PUSH2","bytes":[96,1,80,97,86,91,97],"count":1419},{"pattern":"JUMP JUMPDEST PUSH2 GAS","bytes":[86,91,97,90],"count":1419},{"pattern":"PUSH2 JUMP JUMPDEST PUSH2 GAS","bytes":[97,86,91,97,90],"count":1419},{"pattern":"POP PUSH2 JUMP JUMPDEST PUSH2 GAS","bytes":[80,97,86,91,97,90],"count":1419},{"pattern":"ADD POP PUSH2 JUMP JUMPDEST PUSH2 GAS","bytes":[1,80,97,86,91,97,90],"count":1419},{"pattern":"JUMP JUMPDEST PUSH2 GAS LT","bytes":[86,91,97,90,16],"count":1419},{"pattern":"POP PUSH2 JUMP JUMPDEST PUSH2 GAS LT","bytes":[80,97,86,91,97,90,16],"count":1419},{"pattern":"JUMP JUMPDEST PUSH2 GAS LT PUSH2","bytes":[86,91,97,90,16,97],"count":1419},{"pattern":"PUSH2 JUMP JUMPDEST PUSH2 GAS LT PUSH2","bytes":[97,86,91,97,90,16,97],"count":1419},{"pattern":"JUMP JUMPDEST PUSH2 GAS LT PUSH2 JUMPI","bytes":[86,91,97,90,16,97,87],"count":1419},{"pattern":"PUSH2 JUMPI PUSH1 PUSH1","bytes":[97,87,96,96],"count":1419},{"pattern":"JUMPI PUSH1 PUSH1","bytes":[87,96,96],"count":1419},{"pattern":"JUMPI JUMPDEST","bytes":[87,91],"count":1},{"pattern":"PUSH2 JUMPI JUMPDEST","bytes":[97,87,91],"count":1},{"pattern":"LT PUSH2 JUMPI JUMPDEST","bytes":[16,97,87,91],"count":1},{"pattern":"GAS LT PUSH2 JUMPI JUMPDEST","bytes":[90,16,97,87,91],"count":1},{"pattern":"PUSH2 GAS LT PUSH2 JUMPI JUMPDEST","bytes":[97,90,16,97,87,91],"count":1},{"pattern":"JUMPDEST PUSH2 GAS LT PUSH2 JUMPI JUMPDEST","bytes":[91,97,90,16,97,87,91],"count":1}]}"""

    //    );
        yield return new TestCaseData(
            new byte[][] {
                Prepare.EvmCode.JUMPDEST().PushData(1000).GAS().LT().PUSHx([0, 26]).JUMPI().PushData(23).PushData(7).ADD().POP().PushData(42).PushData(5).ADD().POP().PUSHx([0, 0]).JUMP().JUMPDEST().STOP().Done,
            },
            """{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":272.49,"confidence":0.9375,"stats":[{"pattern":"PUSH1 ADD","bytes":[96,1],"count":2838},{"pattern":"PUSH1 PUSH1 ADD","bytes":[96,96,1],"count":2838},{"pattern":"ADD POP","bytes":[1,80],"count":2838},{"pattern":"PUSH1 ADD POP","bytes":[96,1,80],"count":2838},{"pattern":"PUSH1 PUSH1 ADD POP","bytes":[96,96,1,80],"count":2838},{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2838},{"pattern":"LT PUSH2","bytes":[16,97],"count":1420},{"pattern":"GAS LT PUSH2","bytes":[90,16,97],"count":1420},{"pattern":"PUSH2 GAS LT PUSH2","bytes":[97,90,16,97],"count":1420},{"pattern":"JUMPDEST PUSH2 GAS LT PUSH2","bytes":[91,97,90,16,97],"count":1420},{"pattern":"JUMPDEST PUSH2 GAS","bytes":[91,97,90],"count":1420},{"pattern":"PUSH2 JUMPI","bytes":[97,87],"count":1420},{"pattern":"LT PUSH2 JUMPI","bytes":[16,97,87],"count":1420},{"pattern":"GAS LT PUSH2 JUMPI","bytes":[90,16,97,87],"count":1420},{"pattern":"PUSH2 GAS LT PUSH2 JUMPI","bytes":[97,90,16,97,87],"count":1420},{"pattern":"JUMPDEST PUSH2 GAS LT PUSH2 JUMPI","bytes":[91,97,90,16,97,87],"count":1420},{"pattern":"GAS LT","bytes":[90,16],"count":1420},{"pattern":"JUMPDEST PUSH2","bytes":[91,97],"count":1420},{"pattern":"PUSH2 GAS LT","bytes":[97,90,16],"count":1420},{"pattern":"JUMPDEST PUSH2 GAS LT","bytes":[91,97,90,16],"count":1420},{"pattern":"PUSH2 GAS","bytes":[97,90],"count":1420},{"pattern":"PUSH2 JUMPI PUSH1 PUSH1 ADD POP","bytes":[97,87,96,96,1,80],"count":1419},{"pattern":"PUSH2 JUMPI PUSH1","bytes":[97,87,96],"count":1419},{"pattern":"LT PUSH2 JUMPI PUSH1","bytes":[16,97,87,96],"count":1419},{"pattern":"GAS LT PUSH2 JUMPI PUSH1 PUSH1","bytes":[90,16,97,87,96,96],"count":1419},{"pattern":"JUMPI PUSH1 PUSH1 ADD","bytes":[87,96,96,1],"count":1419},{"pattern":"JUMPI PUSH1 PUSH1 ADD POP","bytes":[87,96,96,1,80],"count":1419},{"pattern":"LT PUSH2 JUMPI PUSH1 PUSH1 ADD POP","bytes":[16,97,87,96,96,1,80],"count":1419},{"pattern":"PUSH1 ADD POP PUSH1","bytes":[96,1,80,96],"count":1419},{"pattern":"POP PUSH1 PUSH1","bytes":[80,96,96],"count":1419},{"pattern":"JUMPI PUSH1 PUSH1 ADD POP PUSH1 PUSH1","bytes":[87,96,96,1,80,96,96],"count":1419},{"pattern":"PUSH1 PUSH1 ADD POP PUSH1 PUSH1 ADD","bytes":[96,96,1,80,96,96,1],"count":1419},{"pattern":"POP PUSH2","bytes":[80,97],"count":1419},{"pattern":"POP PUSH1 PUSH1 ADD POP PUSH2","bytes":[80,96,96,1,80,97],"count":1419},{"pattern":"ADD POP PUSH2 JUMP","bytes":[1,80,97,86],"count":1419},{"pattern":"JUMP JUMPDEST","bytes":[86,91],"count":1419},{"pattern":"GAS LT PUSH2 JUMPI PUSH1","bytes":[90,16,97,87,96],"count":1419},{"pattern":"PUSH2 GAS LT PUSH2 JUMPI PUSH1","bytes":[97,90,16,97,87,96],"count":1419},{"pattern":"JUMPDEST PUSH2 GAS LT PUSH2 JUMPI PUSH1","bytes":[91,97,90,16,97,87,96],"count":1419},{"pattern":"PUSH2 JUMP JUMPDEST PUSH2 GAS LT","bytes":[97,86,91,97,90,16],"count":1419},{"pattern":"JUMPI PUSH1","bytes":[87,96],"count":1419},{"pattern":"LT PUSH2 JUMPI PUSH1 PUSH1","bytes":[16,97,87,96,96],"count":1419},{"pattern":"PUSH2 GAS LT PUSH2 JUMPI PUSH1 PUSH1","bytes":[97,90,16,97,87,96,96],"count":1419},{"pattern":"PUSH2 JUMPI PUSH1 PUSH1 ADD","bytes":[97,87,96,96,1],"count":1419},{"pattern":"LT PUSH2 JUMPI PUSH1 PUSH1 ADD","bytes":[16,97,87,96,96,1],"count":1419},{"pattern":"GAS LT PUSH2 JUMPI PUSH1 PUSH1 ADD","bytes":[90,16,97,87,96,96,1],"count":1419},{"pattern":"POP PUSH1","bytes":[80,96],"count":1419},{"pattern":"ADD POP PUSH1","bytes":[1,80,96],"count":1419},{"pattern":"PUSH1 PUSH1 ADD POP PUSH1","bytes":[96,96,1,80,96],"count":1419},{"pattern":"JUMPI PUSH1 PUSH1 ADD POP PUSH1","bytes":[87,96,96,1,80,96],"count":1419},{"pattern":"PUSH2 JUMPI PUSH1 PUSH1 ADD POP PUSH1","bytes":[97,87,96,96,1,80,96],"count":1419},{"pattern":"ADD POP PUSH1 PUSH1","bytes":[1,80,96,96],"count":1419},{"pattern":"PUSH1 ADD POP PUSH1 PUSH1","bytes":[96,1,80,96,96],"count":1419},{"pattern":"PUSH1 PUSH1 ADD POP PUSH1 PUSH1","bytes":[96,96,1,80,96,96],"count":1419},{"pattern":"POP PUSH1 PUSH1 ADD","bytes":[80,96,96,1],"count":1419},{"pattern":"ADD POP PUSH1 PUSH1 ADD","bytes":[1,80,96,96,1],"count":1419},{"pattern":"PUSH1 ADD POP PUSH1 PUSH1 ADD","bytes":[96,1,80,96,96,1],"count":1419},{"pattern":"POP PUSH1 PUSH1 ADD POP","bytes":[80,96,96,1,80],"count":1419},{"pattern":"ADD POP PUSH1 PUSH1 ADD POP","bytes":[1,80,96,96,1,80],"count":1419},{"pattern":"PUSH1 ADD POP PUSH1 PUSH1 ADD POP","bytes":[96,1,80,96,96,1,80],"count":1419},{"pattern":"ADD POP PUSH2","bytes":[1,80,97],"count":1419},{"pattern":"PUSH1 ADD POP PUSH2","bytes":[96,1,80,97],"count":1419},{"pattern":"PUSH1 PUSH1 ADD POP PUSH2","bytes":[96,96,1,80,97],"count":1419},{"pattern":"ADD POP PUSH1 PUSH1 ADD POP PUSH2","bytes":[1,80,96,96,1,80,97],"count":1419},{"pattern":"PUSH2 JUMP","bytes":[97,86],"count":1419},{"pattern":"POP PUSH2 JUMP","bytes":[80,97,86],"count":1419},{"pattern":"PUSH1 ADD POP PUSH2 JUMP","bytes":[96,1,80,97,86],"count":1419},{"pattern":"PUSH1 PUSH1 ADD POP PUSH2 JUMP","bytes":[96,96,1,80,97,86],"count":1419},{"pattern":"POP PUSH1 PUSH1 ADD POP PUSH2 JUMP","bytes":[80,96,96,1,80,97,86],"count":1419},{"pattern":"PUSH2 JUMP JUMPDEST","bytes":[97,86,91],"count":1419},{"pattern":"POP PUSH2 JUMP JUMPDEST","bytes":[80,97,86,91],"count":1419},{"pattern":"ADD POP PUSH2 JUMP JUMPDEST","bytes":[1,80,97,86,91],"count":1419},{"pattern":"PUSH1 ADD POP PUSH2 JUMP JUMPDEST","bytes":[96,1,80,97,86,91],"count":1419},{"pattern":"PUSH1 PUSH1 ADD POP PUSH2 JUMP JUMPDEST","bytes":[96,96,1,80,97,86,91],"count":1419},{"pattern":"JUMP JUMPDEST PUSH2","bytes":[86,91,97],"count":1419},{"pattern":"PUSH2 JUMP JUMPDEST PUSH2","bytes":[97,86,91,97],"count":1419},{"pattern":"POP PUSH2 JUMP JUMPDEST PUSH2","bytes":[80,97,86,91,97],"count":1419},{"pattern":"ADD POP PUSH2 JUMP JUMPDEST PUSH2","bytes":[1,80,97,86,91,97],"count":1419},{"pattern":"PUSH1 ADD POP PUSH2 JUMP JUMPDEST PUSH2","bytes":[96,1,80,97,86,91,97],"count":1419},{"pattern":"JUMP JUMPDEST PUSH2 GAS","bytes":[86,91,97,90],"count":1419},{"pattern":"PUSH2 JUMP JUMPDEST PUSH2 GAS","bytes":[97,86,91,97,90],"count":1419},{"pattern":"POP PUSH2 JUMP JUMPDEST PUSH2 GAS","bytes":[80,97,86,91,97,90],"count":1419},{"pattern":"ADD POP PUSH2 JUMP JUMPDEST PUSH2 GAS","bytes":[1,80,97,86,91,97,90],"count":1419},{"pattern":"JUMP JUMPDEST PUSH2 GAS LT","bytes":[86,91,97,90,16],"count":1419},{"pattern":"POP PUSH2 JUMP JUMPDEST PUSH2 GAS LT","bytes":[80,97,86,91,97,90,16],"count":1419},{"pattern":"JUMP JUMPDEST PUSH2 GAS LT PUSH2","bytes":[86,91,97,90,16,97],"count":1419},{"pattern":"PUSH2 JUMP JUMPDEST PUSH2 GAS LT PUSH2","bytes":[97,86,91,97,90,16,97],"count":1419},{"pattern":"JUMP JUMPDEST PUSH2 GAS LT PUSH2 JUMPI","bytes":[86,91,97,90,16,97,87],"count":1419},{"pattern":"PUSH2 JUMPI PUSH1 PUSH1","bytes":[97,87,96,96],"count":1419},{"pattern":"JUMPI PUSH1 PUSH1","bytes":[87,96,96],"count":1419},{"pattern":"JUMPI JUMPDEST","bytes":[87,91],"count":1},{"pattern":"PUSH2 JUMPI JUMPDEST","bytes":[97,87,91],"count":1},{"pattern":"LT PUSH2 JUMPI JUMPDEST","bytes":[16,97,87,91],"count":1},{"pattern":"GAS LT PUSH2 JUMPI JUMPDEST","bytes":[90,16,97,87,91],"count":1},{"pattern":"PUSH2 GAS LT PUSH2 JUMPI JUMPDEST","bytes":[97,90,16,97,87,91],"count":1},{"pattern":"JUMPDEST PUSH2 GAS LT PUSH2 JUMPI JUMPDEST","bytes":[91,97,90,16,97,87,91],"count":1}]}"""

        );
    }

}
