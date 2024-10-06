// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Config;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;
using Sigil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using static Nethermind.Evm.CodeAnalysis.IL.IlInfo;
using static Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript.Log;
using Nethermind.Evm.CodeAnalysis.IL.Patterns;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Blockchain;
using Polly;

namespace Nethermind.Evm.Test.CodeAnalysis
{

    internal class AbortDestinationPattern : InstructionChunk // for testing
    {
        public string Name => nameof(AbortDestinationPattern);
        public byte[] Pattern => [(byte)Instruction.JUMPDEST, (byte)Instruction.STOP];
        public byte CallCount { get; set; } = 0;
        public long GasCost(EvmState vmState, IReleaseSpec spec)
        {
            return GasCostOf.JumpDest;
        }

        public void Invoke<T>(EvmState vmState, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec,
            ref int programCounter,
            ref long gasAvailable,
            ref EvmStack<T> stack,
            ref ILChunkExecutionResult result) where T : struct, VirtualMachine.IIsTracing
        {
            CallCount++;

            if (!VirtualMachine<T>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
                result.ExceptionType = EvmExceptionType.OutOfGas;

            programCounter += 2;
            result.ShouldStop = true;
        }
    }
    internal class SomeAfterTwoPush : InstructionChunk
    {
        public string Name => nameof(SomeAfterTwoPush);
        public byte[] Pattern => [96, 96, 01];
        public byte CallCount { get; set; } = 0;

        public long GasCost(EvmState vmState, IReleaseSpec spec)
        {
            long gasCost = GasCostOf.VeryLow * 3;
            return gasCost;
        }

        public void Invoke<T>(EvmState vmState, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec,
            ref int programCounter,
            ref long gasAvailable,
            ref EvmStack<T> stack,
            ref ILChunkExecutionResult result) where T : struct, VirtualMachine.IIsTracing
        {
            CallCount++;

            if (!VirtualMachine<T>.UpdateGas(GasCost(vmState, spec), ref gasAvailable))
                result.ExceptionType = EvmExceptionType.OutOfGas;

            UInt256 lhs = vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1];
            UInt256 rhs = vmState.Env.CodeInfo.MachineCode.Span[programCounter + 3];
            stack.PushUInt256(lhs + rhs);

            programCounter += 5;
        }
    }
    internal class TestBlockChain : VirtualMachineTestsBase
    {
        protected IVMConfig config;
        public TestBlockChain(IVMConfig config)
        {
            this.config = config;
            Setup();
        }

        public TestBlockChain()
        {
            config = new VMConfig();
            Setup();
        }
        public override void Setup()
        {
            base.Setup();
            ILogManager logManager = GetLogManager();

            _blockhashProvider = new TestBlockhashProvider(SpecProvider);
            Machine = new VirtualMachine(_blockhashProvider, SpecProvider, CodeInfoRepository, logManager, config);
            _processor = new TransactionProcessor(SpecProvider, TestState, Machine, CodeInfoRepository, logManager);

            var code = Prepare.EvmCode
                .PushData(23)
                .PushData(7)
                .ADD()
                .MSTORE(0, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray())
                .RETURN(0, 32)
                .STOP().Done;
            TestState.CreateAccount(Address.FromNumber(23), 1000000);
            TestState.InsertCode(Address.FromNumber(23), code, SpecProvider.GenesisSpec);

            IlAnalyzer.AddPattern<SomeAfterTwoPush>();
            IlAnalyzer.AddPattern<EmulatedStaticCJump>();
            IlAnalyzer.AddPattern<EmulatedStaticJump>();
            IlAnalyzer.AddPattern<IsContractCheck>();
            IlAnalyzer.AddPattern<MethodSelector>();
            IlAnalyzer.AddPattern<AbortDestinationPattern>();
        }

        public void Execute<T>(byte[] bytecode, T tracer)
            where T : ITxTracer
        {
            Execute<T>(tracer, bytecode, null, 1_000_000);
        }

        public Address InsertCode(byte[] bytecode)
        {
            var hashcode = Keccak.Compute(bytecode);
            var address = new Address(hashcode);
            var spec = Prague.Instance;
            TestState.CreateAccount(address, 1000000);
            TestState.InsertCode(address, bytecode, spec);
            return address;
        }

        public Hash256 StateRoot
        {
            get
            {
                TestState.Commit(Spec);
                TestState.RecalculateStateRoot();
                return TestState.StateRoot;
            }
        }

    }

    [TestFixture]
    internal class IlEvmTests : TestBlockChain
    {
        private const string AnalyzerField = "_analyzer";
        private const string PatternField = "_patterns";

        [SetUp]
        public override void Setup()
        {
            base.config = new VMConfig()
            {
                IsJitEnabled = true,
                IsPatternMatchingEnabled = true,

                PatternMatchingThreshold = 4,
                JittingThreshold = 256,
            };

            base.Setup();
        }
        public static IEnumerable<(Type, byte[])> GePatBytecodesSamples()
        {
            yield return (null, Prepare.EvmCode
                    .Done);
            yield return (typeof(EmulatedStaticCJump), Prepare.EvmCode
                    .PUSHx([1])
                    .PUSHx([0, 7])
                    .JUMPI()
                    .JUMPDEST()
                    .Done);
            yield return (typeof(EmulatedStaticJump), Prepare.EvmCode
                    .PUSHx([0, 5])
                    .JUMP()
                    .JUMPDEST()
                    .Done);
            yield return (typeof(MethodSelector), Prepare.EvmCode
                    .PushData(0)
                    .PushData(23)
                    .MSTORE()
                    .CALLVALUE()
                    .DUPx(1)
                    .Done);
            yield return (typeof(IsContractCheck), Prepare.EvmCode
                    .EXTCODESIZE(Address.SystemUser)
                    .DUPx(1)
                    .ISZERO()
                    .Done);
        }

        public static IEnumerable<(Instruction?, byte[], EvmExceptionType)> GeJitBytecodesSamples()
        {
            byte[] bytecode = Bytes.FromHexString("0x6060604052361561001f5760e060020a600035046372ea4b8c811461010c575b61011b3460008080670de0b6b3a764000084106101d557600180548101908190556003805433929081101561000257906000526020600020900160006101000a815481600160a060020a0302191690830217905550670de0b6b3a7640000840393508350670de0b6b3a76400006000600082828250540192505081905550600260016000505411151561011d5760038054829081101561000257906000526020600020900160009054906101000a9004600160a060020a0316600160a060020a03166000600060005054604051809050600060405180830381858888f150505080555060016002556101d5565b60018054016060908152602090f35b005b60018054600354910114156101d55760038054600254600101909102900392505b6003546002549003600119018310156101e357600380548490811015610002579082526040517fc2575a0e9e593c00f959f8c92f12db2869c3395a3b0502d05e2516446f71f85b9190910154600160a060020a03169082906706f05b59d3b200009082818181858883f1505090546706f05b59d3b1ffff1901835550506001929092019161013e565b505060028054600101905550505b600080548501905550505050565b506002548154919250600190810190910460001901905b60035460025490036001190183101561029a576003805484908110156100025760009182526040517fc2575a0e9e593c00f959f8c92f12db2869c3395a3b0502d05e2516446f71f85b9190910154600160a060020a03169190838504600019019082818181858883f1505081548486049003600190810190925550600290830183020460001901841415905061028e576001015b600192909201916101fa565b60038054600254810182018083559190829080158290116101c75760008390526101c7907fc2575a0e9e593c00f959f8c92f12db2869c3395a3b0502d05e2516446f71f85b9081019083015b808211156102fa57600081556001016102e6565b509056");
            yield return (Instruction.INVALID, bytecode, EvmExceptionType.None);
        }

        [Test]
        public void All_Stateless_Opcodes_Are_Covered_in_JIT_Tests()
        {
            List<Instruction> instructions = System.Enum.GetValues<Instruction>().ToList();
            instructions.Remove(Instruction.MSTORE);
            instructions.Remove(Instruction.MLOAD);
            instructions.Remove(Instruction.SSTORE);
            instructions.Remove(Instruction.SLOAD);
            instructions.Remove(Instruction.TSTORE);
            instructions.Remove(Instruction.TLOAD);
            instructions.Remove(Instruction.JUMP);
            instructions.Remove(Instruction.JUMPI);
            instructions.Remove(Instruction.JUMPDEST);

            instructions.Add(Instruction.MSTORE | Instruction.MLOAD);
            instructions.Add(Instruction.TSTORE | Instruction.TLOAD);
            instructions.Add(Instruction.SSTORE | Instruction.SLOAD);
            instructions.Add(Instruction.JUMP | Instruction.JUMPDEST);
            instructions.Add(Instruction.JUMPI | Instruction.JUMPDEST);

            var tests = GeJitBytecodesSamples().Select(test => test.Item1);



            List<Instruction> notCovered = new List<Instruction>();
            foreach (var opcode in instructions)
            {
                if (!opcode.IsStateful() && !tests.Contains(opcode))
                {
                    notCovered.Add(opcode);
                }
            }

            Assert.That(notCovered.Count, Is.EqualTo(0), $"[{String.Join(", ", notCovered)}]");
        }

        [Test]
        public async Task Pattern_Analyzer_Find_All_Instance_Of_Pattern()
        {
            byte[] bytecode =
                Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .PushSingle(42)
                    .PushSingle(5)
                    .ADD()
                    .Done;

            CodeInfo codeInfo = new CodeInfo(bytecode);

            await IlAnalyzer.StartAnalysis(codeInfo, IlInfo.ILMode.PAT_MODE, NullLogger.Instance);

            codeInfo.IlInfo.Chunks.Count.Should().Be(2);
        }


        [Test]
        public async Task JIT_Analyzer_Compiles_stateless_bytecode_chunk()
        {
            byte[] bytecode =
                Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .PushSingle(42)
                    .PushSingle(5)
                    .ADD()
                    .Call(Address.FromNumber(23), 10000)
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .PushSingle(42)
                    .PushSingle(5)
                    .ADD()
                    .STOP()
                    .Done;

            CodeInfo codeInfo = new CodeInfo(bytecode);

            await IlAnalyzer.StartAnalysis(codeInfo, IlInfo.ILMode.JIT_MODE, NullLogger.Instance);

            codeInfo.IlInfo.Segments.Count.Should().Be(2);
        }

        [Test]
        public void Execution_Swap_Happens_When_Pattern_Occurs()
        {
            var pattern1 = IlAnalyzer.GetPatternHandler<SomeAfterTwoPush>();
            var pattern2 = IlAnalyzer.GetPatternHandler<EmulatedStaticJump>();
            var pattern3 = IlAnalyzer.GetPatternHandler<EmulatedStaticCJump>();

            byte[] bytecode =
                Prepare.EvmCode
                    .JUMPDEST()
                    .PushSingle(1000)
                    .GAS()
                    .LT()
                    .PUSHx([0, 26])
                    .JUMPI()
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .POP()
                    .PushSingle(42)
                    .PushSingle(5)
                    .ADD()
                    .POP()
                    .PUSHx([0, 0])
                    .JUMP()
                    .JUMPDEST()
                    .STOP()
                    .Done;

            /*
            var hashcode = Keccak.Compute(bytecode);
            var address = new Address(hashcode);
            var spec = Prague.Instance;
            TestState.CreateAccount(address, 1000000);
            TestState.InsertCode(address, bytecode, spec);
            */
            var accumulatedTraces = new List<GethTxTraceEntry>();
            for (int i = 0; i < config.PatternMatchingThreshold * 2; i++)
            {
                var tracer = new GethLikeBlockMemoryTracer(GethTraceOptions.Default);
                ExecuteBlock(tracer, bytecode);
                var traces = tracer.BuildResult().SelectMany(txTrace => txTrace.Entries).Where(tr => tr.IsPrecompiled is not null && !tr.IsPrecompiled.Value).ToList();
                accumulatedTraces.AddRange(traces);
            }

            Assert.That(accumulatedTraces.Count, Is.GreaterThan(0));
        }

        [Test]
        public void All_Opcodes_Have_Metadata()
        {
            Instruction[] instructions = System.Enum.GetValues<Instruction>();
            foreach (var opcode in instructions)
            {
                Assert.That(OpcodeMetadata.Operations.ContainsKey(opcode), Is.True);
            }
        }

        [Test, TestCaseSource(nameof(GeJitBytecodesSamples))]
        public void ILVM_JIT_Execution_Equivalence_Tests((Instruction? opcode, byte[] bytecode, EvmExceptionType _) testcase)
        {
            int repeatCount = 32;

            TestBlockChain standardChain = new TestBlockChain(new VMConfig());
            var address = standardChain.InsertCode(testcase.bytecode);
            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                PatternMatchingThreshold = int.MaxValue,
                IsPatternMatchingEnabled = false,
                JittingThreshold = repeatCount + 1,
                IsJitEnabled = true
            });
            enhancedChain.InsertCode(testcase.bytecode);

            var tracer1 = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
            var tracer2 = new GethLikeTxMemoryTracer(GethTraceOptions.Default);

            var bytecode = Prepare.EvmCode
                .JUMPDEST()
                .Call(address, 100000)
                .POP()
                .PushData(100000)
                .GAS()
                .GT()
                .JUMPI(0)
                .STOP()
                .Done;

            for (var i = 0; i < repeatCount * 2; i++)
            {
                standardChain.Execute<GethLikeTxMemoryTracer>(bytecode, tracer1);
            }

            for (var i = 0; i < repeatCount * 2; i++)
            {
                enhancedChain.Execute<GethLikeTxMemoryTracer>(bytecode, tracer2);
            }

            var normal_traces = tracer1.BuildResult();
            var ilvm_traces = tracer2.BuildResult();

            var actual = standardChain.StateRoot;
            var expected = enhancedChain.StateRoot;

            var ilvm_callsComp = ilvm_traces.Entries.Where(tr => tr.Opcode == "CALL");
            var norm_callsComp = normal_traces.Entries.Where(tr => tr.Opcode == "CALL");

            var zipped = ilvm_callsComp.Zip(norm_callsComp, (ilvm, norm) => (ilvm, norm)).ToList();

            var indexOfChange = zipped.FindIndex(pair => pair.ilvm.Gas != pair.norm.Gas);

            var HasIlvmTraces = ilvm_traces.Entries.Where(tr => tr.SegmentID is not null).Any();

            if (testcase.opcode is not null)
            {
                Assert.That(HasIlvmTraces, Is.True);
            }
            Assert.That(actual, Is.EqualTo(expected));
        }


        [Test, TestCaseSource(nameof(GePatBytecodesSamples))]
        public void ILVM_Pat_Execution_Equivalence_Tests((Type opcode, byte[] bytecode) testcase)
        {
            int repeatCount = 32;

            TestBlockChain standardChain = new TestBlockChain(new VMConfig());
            var address = standardChain.InsertCode(testcase.bytecode);
            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                PatternMatchingThreshold = repeatCount + 1,
                IsPatternMatchingEnabled = true,
                JittingThreshold = int.MaxValue,
                IsJitEnabled = false
            });
            enhancedChain.InsertCode(testcase.bytecode);

            var tracer1 = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
            var tracer2 = new GethLikeTxMemoryTracer(GethTraceOptions.Default);

            var bytecode =
                Prepare.EvmCode
                    .JUMPDEST()
                    .Call(address, 100)
                    .POP()
                    .GAS()
                    .PushData(1000)
                    .LT()
                    .JUMPI(0)
                    .STOP()
                    .Done;

            for (var i = 0; i < repeatCount * 2; i++)
            {
                standardChain.Execute<GethLikeTxMemoryTracer>(bytecode, tracer1);
            }

            for (var i = 0; i < repeatCount * 2; i++)
            {
                enhancedChain.Execute<GethLikeTxMemoryTracer>(bytecode, tracer2);
            }

            var normal_traces = tracer1.BuildResult();
            var ilvm_traces = tracer2.BuildResult();

            var actual = standardChain.StateRoot;
            var expected = enhancedChain.StateRoot;

            var HasIlvmTraces = ilvm_traces.Entries.Where(tr => tr.SegmentID is not null).Any();

            if (testcase.opcode is not null)
            {
                Assert.That(HasIlvmTraces, Is.True);
            }
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void JIT_Mode_Segment_Has_Jump_Into_Another_Segment()
        {
            var address = InsertCode(Prepare.EvmCode
                .PushData(23)
                .PushData(7)
                .ADD()
                .STOP().Done);

            byte[] bytecode =
                Prepare.EvmCode
                    .JUMPDEST()
                    .PushSingle(1000)
                    .GAS()
                    .LT()
                    .JUMPI(58)
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .Call(address, 100)
                    .POP()
                    .PushSingle(42)
                    .PushSingle(5)
                    .ADD()
                    .POP()
                    .JUMP(0)
                    .JUMPDEST()
                    .STOP()
                    .Done;

            var accumulatedTraces = new List<GethTxTraceEntry>();
            for (int i = 0; i <= config.JittingThreshold * 2; i++)
            {
                var tracer = new GethLikeBlockMemoryTracer(GethTraceOptions.Default);
                ExecuteBlock(tracer, bytecode);
                var traces = tracer.BuildResult().SelectMany(txTrace => txTrace.Entries).Where(tr => tr.SegmentID is not null).ToList();
                accumulatedTraces.AddRange(traces);

            }

            // in the last stint gas is almost below 1000
            // it executes segment 0 (0..46)
            // then calls address 23 (segment 0..5 since it is precompiled as well)
            // then it executes segment 48..59 which ends in jump back to pc = 0
            // then it executes segment 0..46 again but this time gas is below 1000
            // it ends jumping to pc = 59 (which is index of AbortDestinationPattern)
            // so the last segment executed is AbortDestinationPattern

            string[] desiredTracePattern = new[]
            {
                "ILEVM_PRECOMPILED_(0x401dfc...0f4912)[0..46]",
                "ILEVM_PRECOMPILED_(0x3dff15...1db9a1)[0..5]",
                "ILEVM_PRECOMPILED_(0x401dfc...0f4912)[48..59]",
                "ILEVM_PRECOMPILED_(0x401dfc...0f4912)[0..46]",
                "AbortDestinationPattern"
            };

            string[] actualTracePattern = accumulatedTraces.TakeLast(5).Select(tr => tr.SegmentID).ToArray();
            Assert.That(actualTracePattern, Is.EqualTo(desiredTracePattern));
        }


        [Test]
        public void JIT_invalid_opcode_results_in_failure()
        {
            byte[] bytecode =
                Prepare.EvmCode
                    .PUSHx() // PUSH0
                    .POP()
                    .STOP()
                    .Done;

            var accumulatedTraces = new List<GethLikeTxTrace>();
            var numberOfRuns = config.JittingThreshold * 1024;
            for (int i = 0; i < numberOfRuns; i++)
            {
                var tracer = new GethLikeBlockMemoryTracer(GethTraceOptions.Default);
                ExecuteBlock(tracer, bytecode, (1024, null));
                var traces = tracer.BuildResult().ToList();
                accumulatedTraces.AddRange(traces);
            }

            var failedTraces = accumulatedTraces.Where(tr => tr.Failed && tr.Entries.Any(subtr => subtr.Error == EvmExceptionType.BadInstruction.ToString())).ToList();
            Assert.That(failedTraces.Count, Is.EqualTo(numberOfRuns));
        }

        [Test]
        public void Execution_Swap_Happens_When_Segments_are_compiled()
        {
            byte[] bytecode =
                Prepare.EvmCode
                    .JUMPDEST()
                    .PushSingle(1000)
                    .GAS()
                    .LT()
                    .PUSHx([0, 26])
                    .JUMPI()
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .POP()
                    .PushSingle(42)
                    .PushSingle(5)
                    .ADD()
                    .POP()
                    .PUSHx([0, 0])
                    .JUMP()
                    .JUMPDEST()
                    .STOP()
                    .Done;

            var accumulatedTraces = new List<GethTxTraceEntry>();
            for (int i = 0; i <= config.JittingThreshold * 32; i++)
            {
                var tracer = new GethLikeBlockMemoryTracer(GethTraceOptions.Default);
                ExecuteBlock(tracer, bytecode);
                var traces = tracer.BuildResult().SelectMany(txTrace => txTrace.Entries).Where(tr => tr.IsPrecompiled is not null && tr.IsPrecompiled.Value).ToList();
                accumulatedTraces.AddRange(traces);
            }

            Assert.That(accumulatedTraces.Count, Is.GreaterThan(0));
        }

        [Test]
        public void Pure_Opcode_Emition_Coveraga()
        {
            Instruction[] instructions =
                System.Enum.GetValues<Instruction>()
                .Where(opcode => !opcode.IsStateful())
                .ToArray();


            List<(Instruction, Exception)> notYetImplemented = [];
            foreach (var instruction in instructions)
            {
                string name = $"ILEVM_TEST_{instruction}";
                OpcodeInfo opcode = new OpcodeInfo(0, instruction, null);
                try
                {
                    ILCompiler.CompileSegment(name, [opcode], []);
                }
                catch (NotSupportedException nse)
                {
                    notYetImplemented.Add((instruction, nse));
                }
                catch (Exception)
                {
                }
            }

            string missingOpcodes = String.Join("; ", notYetImplemented.Select(op => op.Item1.ToString()));
            Assert.That(notYetImplemented.Count == 0, $"{notYetImplemented.Count} opcodes missing: [{missingOpcodes}]");
        }


        [Test]
        public void ILAnalyzer_Initialize_Add_All_Patterns()
        {
            IlAnalyzer.Initialize();
            // get static field _patterns from IlAnalyzer
            var patterns = typeof(IlAnalyzer).GetField(PatternField, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null) as Dictionary<Type, InstructionChunk>;

            Assert.That(patterns.Count, Is.GreaterThan(0));
        }

        [Test, TestCaseSource(nameof(GeJitBytecodesSamples))]
        public void Ensure_Evm_ILvm_Compatibility((Instruction? opcode, byte[] bytecode, EvmExceptionType exceptionType) testcase)
        {
            var blkExCtx = new BlockExecutionContext(BuildBlock(MainnetSpecProvider.CancunActivation, SenderRecipientAndMiner.Default).Header);
            var txExCtx = new TxExecutionContext(blkExCtx, TestItem.AddressA, 23, [TestItem.KeccakH.Bytes.ToArray()]);
            var envExCtx = new ExecutionEnvironment(new CodeInfo(testcase.bytecode), Recipient, Sender, Contract, new ReadOnlyMemory<byte>([1, 2, 3, 4, 5, 6, 7]), txExCtx, 23, 7);
            var stack = new byte[1024 * 32];
            var inputBuffer = envExCtx.InputData;
            var returnBuffer =
                new ReadOnlyMemory<byte>(Enumerable.Range(0, 32)
                .Select(i => (byte)i).ToArray());

            TestState.CreateAccount(Address.FromNumber(1), 1000000);
            TestState.InsertCode(Address.FromNumber(1), testcase.bytecode, Prague.Instance);

            var state = new EvmState(
                1_000_000,
                new ExecutionEnvironment(new CodeInfo(testcase.bytecode), Address.FromNumber(1), Address.FromNumber(1), Address.FromNumber(1), ReadOnlyMemory<byte>.Empty, txExCtx, 0, 0),
                ExecutionType.CALL,
                isTopLevel: false,
                Snapshot.Empty,
                isContinuation: false);

            IVirtualMachine evm = typeof(VirtualMachine).GetField("_evm", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Machine) as IVirtualMachine;
            ICodeInfoRepository codeInfoRepository = typeof(VirtualMachine<VirtualMachine.IsTracing>).GetField("_codeInfoRepository", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(evm) as ICodeInfoRepository;

            state.InitStacks();

            ILEvmState iLEvmState = new ILEvmState(SpecProvider.ChainId, state, EvmExceptionType.None, 0, 100000, ref returnBuffer);
            var metadata = IlAnalyzer.StripByteCode(testcase.bytecode);
            var bytecode = metadata.Item1.TakeWhile(instruction => instruction.Operation != Instruction.CALL).ToArray();
            var ctx = ILCompiler.CompileSegment("ILEVM_TEST", bytecode, metadata.Item2);
            ctx.PrecompiledSegment(ref iLEvmState, _blockhashProvider, TestState, codeInfoRepository, Prague.Instance, ctx.Data);

            Assert.That(iLEvmState.EvmException == testcase.exceptionType);
        }

        [Test]
        public void Custom_Temporaty_Test()
        {
            byte[] testcase = Prepare.EvmCode
                .FromCode("608060405234801561001057600080fd5b506004361061002b5760003560e01c8063f87576e814610030575b600080fd5b61004361003e366004611bf0565b610055565b60405190815260200160405180910390f35b60008088116100eb576040517f08c379a000000000000000000000000000000000000000000000000000000000815260206004820152602760248201527f4b79626572466f726d756c613a20494e53554646494349454e545f494e50555460448201527f5f414d4f554e540000000000000000000000000000000000000000000000000060648201526084015b60405180910390fd5b6000871180156100fb5750600086115b610186576040517f08c379a0000000000000000000000000000000000000000000000000000000008152602060048201526024808201527f4b79626572466f726d756c613a20494e53554646494349454e545f4c4951554960448201527f444954590000000000000000000000000000000000000000000000000000000060648201526084016100e2565b60006101aa6101958585611c90565b63ffffffff168a61027790919063ffffffff16565b90508463ffffffff168663ffffffff1614156101f9576101dd816101d78a63ffffffff8088169061027716565b9061028a565b6101e78883610277565b6101f19190611ce4565b91505061026c565b60008080610214846101d78d63ffffffff808b169061027716565b90506102348161022d8d63ffffffff808b169061027716565b8b8b610296565b909350915060006102458b85610277565b905060ff83168b901b846102598284611cf8565b6102639190611ce4565b96505050505050505b979650505050505050565b60006102838284611d0f565b9392505050565b60006102838284611d4c565b60008084861015610303576040517f08c379a000000000000000000000000000000000000000000000000000000000815260206004820152601b60248201527f6e6f7420737570706f7274205f626173654e203c205f6261736544000000000060448201526064016100e2565b700200000000000000000000000000000000861061032057600080fd5b6000808661033e6f800000000000000000000000000000008a611d0f565b6103489190611ce4565b905070015bf0a8b1457695355fb8ac404e7a79e38110156103735761036c81610413565b915061037f565b61037c81610b1f565b91505b60008563ffffffff168763ffffffff168461039a9190611d0f565b6103a49190611ce4565b90507008000000000000000000000000000000008110156103d6576103c881610cd2565b607f9450945050505061040a565b60006103e182611450565b90506103fd6103f182607f611d64565b60ff1683901c82611503565b9550935061040a92505050565b94509492505050565b6000808080806fd3094c70f034de4b96ff7d5b6f99fcd886106104845761044a6f4000000000000000000000000000000085611d4c565b93506fd3094c70f034de4b96ff7d5b6f99fcd86104776f8000000000000000000000000000000088611d0f565b6104819190611ce4565b95505b6fa45af1e1f40c333b3de1db4dd55f29a786106104ef576104b56f2000000000000000000000000000000085611d4c565b93506fa45af1e1f40c333b3de1db4dd55f29a76104e26f8000000000000000000000000000000088611d0f565b6104ec9190611ce4565b95505b6f910b022db7ae67ce76b441c27035c6a1861061055a576105206f1000000000000000000000000000000085611d4c565b93506f910b022db7ae67ce76b441c27035c6a161054d6f8000000000000000000000000000000088611d0f565b6105579190611ce4565b95505b6f88415abbe9a76bead8d00cf112e4d4a886106105c55761058b6f0800000000000000000000000000000085611d4c565b93506f88415abbe9a76bead8d00cf112e4d4a86105b86f8000000000000000000000000000000088611d0f565b6105c29190611ce4565b95505b6f84102b00893f64c705e841d5d4064bd38610610630576105f66f0400000000000000000000000000000085611d4c565b93506f84102b00893f64c705e841d5d4064bd36106236f8000000000000000000000000000000088611d0f565b61062d9190611ce4565b95505b6f8204055aaef1c8bd5c3259f4822735a2861061069b576106616f0200000000000000000000000000000085611d4c565b93506f8204055aaef1c8bd5c3259f4822735a261068e6f8000000000000000000000000000000088611d0f565b6106989190611ce4565b95505b6f810100ab00222d861931c15e39b44e998610610706576106cc6f0100000000000000000000000000000085611d4c565b93506f810100ab00222d861931c15e39b44e996106f96f8000000000000000000000000000000088611d0f565b6107039190611ce4565b95505b6f808040155aabbbe9451521693554f7338610610770576107366e80000000000000000000000000000085611d4c565b93506f808040155aabbbe9451521693554f7336107636f8000000000000000000000000000000088611d0f565b61076d9190611ce4565b95505b61078a6f8000000000000000000000000000000087611cf8565b92508291506f800000000000000000000000000000006107aa8380611d0f565b6107b49190611ce4565b90507001000000000000000000000000000000006107d28482611cf8565b6107dc9084611d0f565b6107e69190611ce4565b6107f09085611d4c565b93506f8000000000000000000000000000000061080d8284611d0f565b6108179190611ce4565b9150700200000000000000000000000000000000610845846faaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa611cf8565b61084f9084611d0f565b6108599190611ce4565b6108639085611d4c565b93506f800000000000000000000000000000006108808284611d0f565b61088a9190611ce4565b91507003000000000000000000000000000000006108b8846f99999999999999999999999999999999611cf8565b6108c29084611d0f565b6108cc9190611ce4565b6108d69085611d4c565b93506f800000000000000000000000000000006108f38284611d0f565b6108fd9190611ce4565b915070040000000000000000000000000000000061092b846f92492492492492492492492492492492611cf8565b6109359084611d0f565b61093f9190611ce4565b6109499085611d4c565b93506f800000000000000000000000000000006109668284611d0f565b6109709190611ce4565b915070050000000000000000000000000000000061099e846f8e38e38e38e38e38e38e38e38e38e38e611cf8565b6109a89084611d0f565b6109b29190611ce4565b6109bc9085611d4c565b93506f800000000000000000000000000000006109d98284611d0f565b6109e39190611ce4565b9150700600000000000000000000000000000000610a11846f8ba2e8ba2e8ba2e8ba2e8ba2e8ba2e8b611cf8565b610a1b9084611d0f565b610a259190611ce4565b610a2f9085611d4c565b93506f80000000000000000000000000000000610a4c8284611d0f565b610a569190611ce4565b9150700700000000000000000000000000000000610a84846f89d89d89d89d89d89d89d89d89d89d89611cf8565b610a8e9084611d0f565b610a989190611ce4565b610aa29085611d4c565b93506f80000000000000000000000000000000610abf8284611d0f565b610ac99190611ce4565b9150700800000000000000000000000000000000610af7846f88888888888888888888888888888888611cf8565b610b019084611d0f565b610b0b9190611ce4565b610b159085611d4c565b9695505050505050565b60006f80000000000000000000000000000000821015610b9b576040517f08c379a000000000000000000000000000000000000000000000000000000000815260206004820152601760248201527f6e6f7420737570706f72742078203c2046495845445f3100000000000000000060448201526064016100e2565b60007001000000000000000000000000000000008310610c03576000610bd9610bd46f8000000000000000000000000000000086611ce4565b611b6a565b60ff811694851c94909150610bff906f8000000000000000000000000000000090611d0f565b9150505b6f80000000000000000000000000000000831115610c9d57607f5b60ff811615610c9b576f80000000000000000000000000000000610c428580611d0f565b610c4c9190611ce4565b93507001000000000000000000000000000000008410610c8b57600193841c93610c769082611d64565b60ff166001901b82610c889190611d4c565b91505b610c9481611d87565b9050610c1e565b505b6f05b9de1d10bf4103d647b0955897ba80610cc86f03f80fe03f80fe03f80fe03f80fe03f883611d0f565b6102839190611ce4565b6000808080610cf16f1000000000000000000000000000000086611dc2565b91508190506f80000000000000000000000000000000610d118280611d0f565b610d1b9190611ce4565b9050610d2f816710e1b3be415a0000611d0f565b610d399084611d4c565b92506f80000000000000000000000000000000610d568383611d0f565b610d609190611ce4565b9050610d74816705a0913f6b1e0000611d0f565b610d7e9084611d4c565b92506f80000000000000000000000000000000610d9b8383611d0f565b610da59190611ce4565b9050610db981670168244fdac78000611d0f565b610dc39084611d4c565b92506f80000000000000000000000000000000610de08383611d0f565b610dea9190611ce4565b9050610dfd81664807432bc18000611d0f565b610e079084611d4c565b92506f80000000000000000000000000000000610e248383611d0f565b610e2e9190611ce4565b9050610e4181660c0135dca04000611d0f565b610e4b9084611d4c565b92506f80000000000000000000000000000000610e688383611d0f565b610e729190611ce4565b9050610e85816601b707b1cdc000611d0f565b610e8f9084611d4c565b92506f80000000000000000000000000000000610eac8383611d0f565b610eb69190611ce4565b9050610ec8816536e0f639b800611d0f565b610ed29084611d4c565b92506f80000000000000000000000000000000610eef8383611d0f565b610ef99190611ce4565b9050610f0b81650618fee9f800611d0f565b610f159084611d4c565b92506f80000000000000000000000000000000610f328383611d0f565b610f3c9190611ce4565b9050610f4d81649c197dcc00611d0f565b610f579084611d4c565b92506f80000000000000000000000000000000610f748383611d0f565b610f7e9190611ce4565b9050610f8f81640e30dce400611d0f565b610f999084611d4c565b92506f80000000000000000000000000000000610fb68383611d0f565b610fc09190611ce4565b9050610fd18164012ebd1300611d0f565b610fdb9084611d4c565b92506f80000000000000000000000000000000610ff88383611d0f565b6110029190611ce4565b9050611012816317499f00611d0f565b61101c9084611d4c565b92506f800000000000000000000000000000006110398383611d0f565b6110439190611ce4565b9050611053816301a9d480611d0f565b61105d9084611d4c565b92506f8000000000000000000000000000000061107a8383611d0f565b6110849190611ce4565b905061109381621c6380611d0f565b61109d9084611d4c565b92506f800000000000000000000000000000006110ba8383611d0f565b6110c49190611ce4565b90506110d3816201c638611d0f565b6110dd9084611d4c565b92506f800000000000000000000000000000006110fa8383611d0f565b6111049190611ce4565b905061111281611ab8611d0f565b61111c9084611d4c565b92506f800000000000000000000000000000006111398383611d0f565b6111439190611ce4565b90506111518161017c611d0f565b61115b9084611d4c565b92506f800000000000000000000000000000006111788383611d0f565b6111829190611ce4565b905061118f816014611d0f565b6111999084611d4c565b92506f800000000000000000000000000000006111b68383611d0f565b6111c09190611ce4565b90506111cd816001611d0f565b6111d79084611d4c565b92506f80000000000000000000000000000000826111fd6721c3677c82b4000086611ce4565b6112079190611d4c565b6112119190611d4c565b92506f100000000000000000000000000000008516156112655770018ebef9eac820ae8682b9793ac6d1e776611258847001c3d6a24ed82218787d624d3e5eba95f9611d0f565b6112629190611ce4565b92505b6f200000000000000000000000000000008516156112b7577001368b2fc6f9609fe7aceb46aa619baed46112aa8470018ebef9eac820ae8682b9793ac6d1e778611d0f565b6112b49190611ce4565b92505b6f40000000000000000000000000000000851615611308576fbc5ab1b16779be3575bd8f0520a9f21f6112fb847001368b2fc6f9609fe7aceb46aa619baed5611d0f565b6113059190611ce4565b92505b6f80000000000000000000000000000000851615611358576f454aaa8efe072e7f6ddbab84b40a55c961134b846fbc5ab1b16779be3575bd8f0520a9f21e611d0f565b6113559190611ce4565b92505b7001000000000000000000000000000000008516156113a9576f0960aadc109e7a3bf4578099615711ea61139c846f454aaa8efe072e7f6ddbab84b40a55c5611d0f565b6113a69190611ce4565b92505b7002000000000000000000000000000000008516156113f9576e2bf84208204f5977f9a8cf01fdce3d6113ec846f0960aadc109e7a3bf4578099615711d7611d0f565b6113f69190611ce4565b92505b700400000000000000000000000000000000851615611447576d03c6ab775dd0b95b4cbee7e65d1161143a846e2bf84208204f5977f9a8cf01fdc307611d0f565b6114449190611ce4565b92505b50909392505050565b60006020607f5b60ff8116611466836001611dd6565b60ff1610156114b9576000600261147d8385611dd6565b6114879190611dfb565b90508460008260ff16608081106114a0576114a0611e1d565b0154106114af578092506114b3565b8091505b50611457565b8360008260ff16608081106114d0576114d0611e1d565b0154106114de579392505050565b8360008360ff16608081106114f5576114f5611e1d565b01541061002b575092915050565b6000828160ff84166115158380611d0f565b901c9150611533826f03442c4e6074a82f1797f72ac0000000611d0f565b61153d9082611d4c565b905060ff841661154d8684611d0f565b901c915061156b826f0116b96f757c380fb287fd0e40000000611d0f565b6115759082611d4c565b905060ff84166115858684611d0f565b901c91506115a2826e45ae5bdd5f0e03eca1ff4390000000611d0f565b6115ac9082611d4c565b905060ff84166115bc8684611d0f565b901c91506115d9826e0defabf91302cd95b9ffda50000000611d0f565b6115e39082611d4c565b905060ff84166115f38684611d0f565b901c9150611610826e02529ca9832b22439efff9b8000000611d0f565b61161a9082611d4c565b905060ff841661162a8684611d0f565b901c9150611646826d54f1cf12bd04e516b6da88000000611d0f565b6116509082611d4c565b905060ff84166116608684611d0f565b901c915061167c826d0a9e39e257a09ca2d6db51000000611d0f565b6116869082611d4c565b905060ff84166116968684611d0f565b901c91506116b2826d012e066e7b839fa050c309000000611d0f565b6116bc9082611d4c565b905060ff84166116cc8684611d0f565b901c91506116e7826c1e33d7d926c329a1ad1a800000611d0f565b6116f19082611d4c565b905060ff84166117018684611d0f565b901c915061171c826c02bee513bdb4a6b19b5f800000611d0f565b6117269082611d4c565b905060ff84166117368684611d0f565b901c9150611750826b3a9316fa79b88eccf2a00000611d0f565b61175a9082611d4c565b905060ff841661176a8684611d0f565b901c9150611784826b048177ebe1fa812375200000611d0f565b61178e9082611d4c565b905060ff841661179e8684611d0f565b901c91506117b7826a5263fe90242dcbacf00000611d0f565b6117c19082611d4c565b905060ff84166117d18684611d0f565b901c91506117ea826a057e22099c030d94100000611d0f565b6117f49082611d4c565b905060ff84166118048684611d0f565b901c915061181c826957e22099c030d9410000611d0f565b6118269082611d4c565b905060ff84166118368684611d0f565b901c915061184e8269052b6b54569976310000611d0f565b6118589082611d4c565b905060ff84166118688684611d0f565b901c915061187f82684985f67696bf748000611d0f565b6118899082611d4c565b905060ff84166118998684611d0f565b901c91506118b0826803dea12ea99e498000611d0f565b6118ba9082611d4c565b905060ff84166118ca8684611d0f565b901c91506118e0826731880f2214b6e000611d0f565b6118ea9082611d4c565b905060ff84166118fa8684611d0f565b901c91506119108267025bcff56eb36000611d0f565b61191a9082611d4c565b905060ff841661192a8684611d0f565b901c915061193f82661b722e10ab1000611d0f565b6119499082611d4c565b905060ff84166119598684611d0f565b901c915061196e826601317c70077000611d0f565b6119789082611d4c565b905060ff84166119888684611d0f565b901c915061199c82650cba84aafa00611d0f565b6119a69082611d4c565b905060ff84166119b68684611d0f565b901c91506119c9826482573a0a00611d0f565b6119d39082611d4c565b905060ff84166119e38684611d0f565b901c91506119f6826405035ad900611d0f565b611a009082611d4c565b905060ff8416611a108684611d0f565b901c9150611a2282632f881b00611d0f565b611a2c9082611d4c565b905060ff8416611a3c8684611d0f565b901c9150611a4e826301b29340611d0f565b611a589082611d4c565b905060ff8416611a688684611d0f565b901c9150611a7982620efc40611d0f565b611a839082611d4c565b905060ff8416611a938684611d0f565b901c9150611aa382617fe0611d0f565b611aad9082611d4c565b905060ff8416611abd8684611d0f565b901c9150611acd82610420611d0f565b611ad79082611d4c565b905060ff8416611ae78684611d0f565b901c9150611af6826021611d0f565b611b009082611d4c565b905060ff8416611b108684611d0f565b901c9150611b1f826001611d0f565b611b299082611d4c565b9050600160ff85161b85611b4d6f0688589cc0e9505e2f2fee558000000084611ce4565b611b579190611d4c565b611b619190611d4c565b95945050505050565b600080610100831015611b9d575b6001831115611b9857600192831c92611b919082611dd6565b9050611b78565b611bd1565b60805b60ff811615611bcf57600160ff82161b8410611bc45760ff81169390931c92908117905b60011c607f16611ba0565b505b92915050565b803563ffffffff81168114611beb57600080fd5b919050565b600080600080600080600060e0888a031215611c0b57600080fd5b873596506020880135955060408801359450611c2960608901611bd7565b9350611c3760808901611bd7565b9250611c4560a08901611bd7565b9150611c5360c08901611bd7565b905092959891949750929550565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052601160045260246000fd5b600063ffffffff83811690831681811015611cad57611cad611c61565b039392505050565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052601260045260246000fd5b600082611cf357611cf3611cb5565b500490565b600082821015611d0a57611d0a611c61565b500390565b6000817fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff0483118215151615611d4757611d47611c61565b500290565b60008219821115611d5f57611d5f611c61565b500190565b600060ff821660ff841680821015611d7e57611d7e611c61565b90039392505050565b600060ff821680611d9a57611d9a611c61565b7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff0192915050565b600082611dd157611dd1611cb5565b500690565b600060ff821660ff84168060ff03821115611df357611df3611c61565b019392505050565b600060ff831680611e0e57611e0e611cb5565b8060ff84160491505092915050565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052603260045260246000fdfea26469706673582212204fca7f478a4263e8f6474cc3ff11c07adfad3f0db21fb2f392c901018221822d64736f6c63430008090033")
                .Done;

            var codeinfo = new CodeInfo(testcase);
            var blkExCtx = new BlockExecutionContext(BuildBlock(MainnetSpecProvider.CancunActivation, SenderRecipientAndMiner.Default).Header);
            var txExCtx = new TxExecutionContext(blkExCtx, TestItem.AddressA, 23, [TestItem.KeccakH.Bytes.ToArray()]);
            var envExCtx = new ExecutionEnvironment(codeinfo, Recipient, Sender, Contract, new ReadOnlyMemory<byte>([1, 2, 3, 4, 5, 6, 7]), txExCtx, 23, 7);
            var stack = new byte[1024 * 32];
            var inputBuffer = envExCtx.InputData;
            var returnBuffer =
                new ReadOnlyMemory<byte>(Enumerable.Range(0, 32)
                .Select(i => (byte)i).ToArray());

            TestState.CreateAccount(Address.FromNumber(1), 1000000);
            TestState.InsertCode(Address.FromNumber(1), testcase, Prague.Instance);

            var state = new EvmState(
                1_000_000,
                new ExecutionEnvironment(codeinfo, Address.FromNumber(1), Address.FromNumber(1), Address.FromNumber(1), ReadOnlyMemory<byte>.Empty, txExCtx, 0, 0),
                ExecutionType.CALL,
                isTopLevel: false,
                Snapshot.Empty,
                isContinuation: false);

            IVirtualMachine evm = typeof(VirtualMachine).GetField("_evm", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Machine) as IVirtualMachine;
            ICodeInfoRepository codeInfoRepository = typeof(VirtualMachine<VirtualMachine.IsTracing>).GetField("_codeInfoRepository", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(evm) as ICodeInfoRepository;

            state.InitStacks();

            ILEvmState iLEvmState = new ILEvmState(SpecProvider.ChainId, state, EvmExceptionType.None, 0, 100000, ref returnBuffer);
            IlAnalyzer.Analysis(codeinfo, 2, NullLogger.Instance);
            var metadata = IlAnalyzer.StripByteCode(testcase);
            var ctx = ILCompiler.CompileSegment("ILEVM_TEST", metadata.Item1, metadata.Item2);
            ctx.PrecompiledSegment(ref iLEvmState, _blockhashProvider, TestState, codeInfoRepository, Prague.Instance, ctx.Data);
        }
    }
}
