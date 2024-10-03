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
                .PushData(23)
                .DUPx(1)
                .STOP()
                .Done;

            var blkExCtx = new BlockExecutionContext(BuildBlock(MainnetSpecProvider.CancunActivation, SenderRecipientAndMiner.Default).Header);
            var txExCtx = new TxExecutionContext(blkExCtx, TestItem.AddressA, 23, [TestItem.KeccakH.Bytes.ToArray()]);
            var envExCtx = new ExecutionEnvironment(new CodeInfo(testcase), Recipient, Sender, Contract, new ReadOnlyMemory<byte>([1, 2, 3, 4, 5, 6, 7]), txExCtx, 23, 7);
            var stack = new byte[1024 * 32];
            var inputBuffer = envExCtx.InputData;
            var returnBuffer =
                new ReadOnlyMemory<byte>(Enumerable.Range(0, 32)
                .Select(i => (byte)i).ToArray());

            TestState.CreateAccount(Address.FromNumber(1), 1000000);
            TestState.InsertCode(Address.FromNumber(1), testcase, Prague.Instance);

            var state = new EvmState(
                1_000_000,
                new ExecutionEnvironment(new CodeInfo(testcase), Address.FromNumber(1), Address.FromNumber(1), Address.FromNumber(1), ReadOnlyMemory<byte>.Empty, txExCtx, 0, 0),
                ExecutionType.CALL,
                isTopLevel: false,
                Snapshot.Empty,
                isContinuation: false);

            IVirtualMachine evm = typeof(VirtualMachine).GetField("_evm", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Machine) as IVirtualMachine;
            ICodeInfoRepository codeInfoRepository = typeof(VirtualMachine<VirtualMachine.IsTracing>).GetField("_codeInfoRepository", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(evm) as ICodeInfoRepository;

            state.InitStacks();

            ILEvmState iLEvmState = new ILEvmState(SpecProvider.ChainId, state, EvmExceptionType.None, 0, 100000, ref returnBuffer);
            var metadata = IlAnalyzer.StripByteCode(testcase);
            var ctx = ILCompiler.CompileSegment("ILEVM_TEST", metadata.Item1, metadata.Item2);
            ctx.PrecompiledSegment(ref iLEvmState, _blockhashProvider, TestState, codeInfoRepository, Prague.Instance, ctx.Data);
        }
    }
}
