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
            long gasCost = GasCostOf.VeryLow + GasCostOf.VeryLow + GasCostOf.Base;
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

    internal class MethodSelector : InstructionChunk
    {
        public string Name => nameof(MethodSelector);
        public byte[] Pattern => [(byte)Instruction.PUSH1, (byte)Instruction.PUSH1, (byte)Instruction.MSTORE, (byte)Instruction.CALLVALUE, (byte)Instruction.DUP1];
        public byte CallCount { get; set; } = 0;

        public long GasCost(EvmState vmState, IReleaseSpec spec)
        {
            long gasCost = GasCostOf.VeryLow + GasCostOf.VeryLow + GasCostOf.VeryLow + GasCostOf.Base + GasCostOf.VeryLow;
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

            byte value = vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1];
            byte location = vmState.Env.CodeInfo.MachineCode.Span[programCounter + 3];
            VirtualMachine<T>.UpdateMemoryCost(ref vmState.Memory, ref gasAvailable, 0, 32);
            vmState.Memory.SaveByte(location, value);
            stack.PushUInt256(vmState.Env.Value);
            stack.PushUInt256(vmState.Env.Value);

            programCounter += 2 + 2 + 1 + 1 + 1;
        }
    }

    internal class IsContractCheck : InstructionChunk
    {
        public string Name => nameof(IsContractCheck);
        public byte[] Pattern => [(byte)Instruction.EXTCODESIZE, (byte)Instruction.DUP1, (byte)Instruction.ISZERO];
        public byte CallCount { get; set; } = 0;

        public long GasCost(EvmState vmState, IReleaseSpec spec)
        {
            long gasCost = spec.GetExtCodeCost() + GasCostOf.VeryLow + GasCostOf.Base;
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

            Address address = stack.PopAddress();
            int contractCodeSize = worldState.GetCode(address).Length;
            stack.PushUInt32(contractCodeSize);
            if (contractCodeSize == 0)
            {
                stack.PushOne();
            }
            else
            {
                stack.PushZero();
            }

            programCounter += 3;
        }

    }
    internal class EmulatedStaticJump : InstructionChunk
    {
        public string Name => nameof(EmulatedStaticJump);
        public byte[] Pattern => [(byte)Instruction.PUSH2, (byte)Instruction.JUMP];
        public byte CallCount { get; set; } = 0;

        public long GasCost(EvmState vmState, IReleaseSpec spec)
        {
            long gasCost = GasCostOf.VeryLow + GasCostOf.Mid;
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

            int jumpdestionation = (vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1] << 8) | vmState.Env.CodeInfo.MachineCode.Span[programCounter + 2];
            if (jumpdestionation < vmState.Env.CodeInfo.MachineCode.Length && vmState.Env.CodeInfo.MachineCode.Span[jumpdestionation] == (byte)Instruction.JUMPDEST)
            {
                programCounter = jumpdestionation;
            }
            else
            {
                result.ExceptionType = EvmExceptionType.InvalidJumpDestination;
            }
        }

    }
    internal class EmulatedStaticCJump : InstructionChunk
    {
        public string Name => nameof(EmulatedStaticCJump);
        public byte[] Pattern => [(byte)Instruction.PUSH2, (byte)Instruction.JUMPI];
        public byte CallCount { get; set; } = 0;

        public long GasCost(EvmState vmState, IReleaseSpec spec)
        {
            long gasCost = GasCostOf.VeryLow + GasCostOf.High;
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

            stack.PopUInt256(out UInt256 condition);
            int jumpdestionation = (vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1] << 8) | vmState.Env.CodeInfo.MachineCode.Span[programCounter + 2];
            if (!condition.IsZero)
            {
                if (jumpdestionation < vmState.Env.CodeInfo.MachineCode.Length && vmState.Env.CodeInfo.MachineCode.Span[jumpdestionation] == (byte)Instruction.JUMPDEST)
                {
                    programCounter = jumpdestionation;
                }
                else
                {
                    result.ExceptionType = EvmExceptionType.InvalidJumpDestination;
                }
            }
            else
            {
                programCounter += 4;
            }
        }
    }



    [TestFixture]
    internal class IlEvmTests : VirtualMachineTestsBase
    {
        private const string AnalyzerField = "_analyzer";
        private readonly IVMConfig _vmConfig = new VMConfig()
        {
            IsJitEnabled = true,
            IsPatternMatchingEnabled = true,

            PatternMatchingThreshold = 4,
            JittingThreshold = 256,
        };

        [SetUp]
        public override void Setup()
        {
            base.Setup();
            ILogManager logManager = GetLogManager();

            _blockhashProvider = new TestBlockhashProvider(SpecProvider);
            Machine = new VirtualMachine(_blockhashProvider, SpecProvider, CodeInfoRepository, logManager, _vmConfig);
            _processor = new TransactionProcessor(SpecProvider, TestState, Machine, CodeInfoRepository, logManager);

            var code = Prepare.EvmCode
                .PushData(23)
                .PushData(7)
                .ADD()
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

            await IlAnalyzer.StartAnalysis(codeInfo, IlInfo.ILMode.PatternMatching, NullTxTracer.Instance);

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

            await IlAnalyzer.StartAnalysis(codeInfo, IlInfo.ILMode.SubsegmentsCompiling, NullTxTracer.Instance);

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


            var accumulatedTraces = new List<GethTxTraceEntry>();
            for (int i = 0; i < IlAnalyzer.CompoundOpThreshold * 2; i++)
            {
                var tracer = new GethLikeBlockMemoryTracer(GethTraceOptions.Default);
                ExecuteBlock(tracer, bytecode);
                var traces = tracer.BuildResult().SelectMany(txTrace => txTrace.Entries).Where(tr => tr.IsPrecompiled is not null && !tr.IsPrecompiled.Value).ToList();
                accumulatedTraces.AddRange(traces);
            }

            Assert.Greater(accumulatedTraces.Count, 0);
        }

        [Test]
        public void JIT_Mode_Segment_Has_Jump_Into_Another_Segment()
        {
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
                    .Call(Address.FromNumber(23), 100)
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
            for (int i = 0; i <= IlAnalyzer.IlCompilerThreshold * 32; i++)
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
                "ILEVM_PRECOMPILED_(0x195fe3...9dbe75)[0..46]",
                "ILEVM_PRECOMPILED_(0x3dff15...1db9a1)[0..5]",
                "ILEVM_PRECOMPILED_(0x195fe3...9dbe75)[48..59]",
                "ILEVM_PRECOMPILED_(0x195fe3...9dbe75)[0..46]",
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
            var numberOfRuns = IlAnalyzer.IlCompilerThreshold * 1024;
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
            for (int i = 0; i <= IlAnalyzer.IlCompilerThreshold * 32; i++)
            {
                var tracer = new GethLikeBlockMemoryTracer(GethTraceOptions.Default);
                ExecuteBlock(tracer, bytecode);
                var traces = tracer.BuildResult().SelectMany(txTrace => txTrace.Entries).Where(tr => tr.IsPrecompiled is not null && tr.IsPrecompiled.Value).ToList();
                accumulatedTraces.AddRange(traces);
            }


            Assert.Greater(accumulatedTraces.Count, 0);
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


        public static IEnumerable<(Instruction?, byte[])> GetBytecodes()
        {
            yield return (null, Prepare.EvmCode
                    .Done);
            yield return (Instruction.PUSH32, Prepare.EvmCode
                    .PushSingle(1)
                    .Done);
            yield return (Instruction.ISZERO, Prepare.EvmCode
                    .ISZERO(7)
                    .ISZERO(0)
                    .ISZERO(7)
                    .Done);
            yield return (Instruction.SUB, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .SUB()
                    .Done);

            yield return (Instruction.ADD, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .Done);

            yield return (Instruction.ADDMOD, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .PushSingle(5)
                    .ADDMOD()
                    .Done);

            yield return (Instruction.MUL, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .MUL()
                    .Done);

            yield return (Instruction.EXP, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .EXP()
                    .Done);

            yield return (Instruction.MOD, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .MOD()
                    .Done);

            yield return (Instruction.DIV, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .DIV()
                    .Done);

            yield return (Instruction.MSTORE, Prepare.EvmCode
                    .MSTORE(0, ((UInt256)23).PaddedBytes(32))
                    .Done);

            yield return (Instruction.MLOAD, Prepare.EvmCode
                    .MSTORE(0, ((UInt256)23).PaddedBytes(32))
                    .MLOAD(0)
                    .Done);

            yield return (Instruction.MCOPY, Prepare.EvmCode
                    .MSTORE(0, ((UInt256)23).PaddedBytes(32))
                    .MCOPY(32, 0, 32)
                    .Done);

            yield return (Instruction.EQ, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .EQ()
                    .Done);

            yield return (Instruction.GT, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .GT()
                    .Done);

            yield return (Instruction.LT, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .LT()
                    .Done);

            yield return (Instruction.NOT, Prepare.EvmCode
                    .PushSingle(1)
                    .NOT()
                    .Done);

            yield return (Instruction.BLOBHASH, Prepare.EvmCode
                    .PushSingle(0)
                    .BLOBHASH()
                    .Done);

            yield return (Instruction.BLOCKHASH, Prepare.EvmCode
                    .BLOCKHASH(0)
                    .Done);

            yield return (Instruction.CALLDATACOPY, Prepare.EvmCode
                    .CALLDATACOPY(0, 0, 32)
                    .Done);

            yield return (Instruction.CALLDATALOAD, Prepare.EvmCode
                    .CALLDATALOAD(0)
                    .Done);

            yield return (Instruction.MSIZE, Prepare.EvmCode
                    .MSIZE()
                    .Done);

            yield return (Instruction.GASPRICE, Prepare.EvmCode
                    .GASPRICE()
                    .Done);

            yield return (Instruction.CODESIZE, Prepare.EvmCode
                    .CODESIZE()
                    .Done);

            yield return (Instruction.PC, Prepare.EvmCode
                    .PC()
                    .Done);

            yield return (Instruction.COINBASE, Prepare.EvmCode
                    .COINBASE()
                    .Done);

            yield return (Instruction.TIMESTAMP, Prepare.EvmCode
                    .TIMESTAMP()
                    .Done);

            yield return (Instruction.NUMBER, Prepare.EvmCode
                    .NUMBER()
                    .Done);

            yield return (Instruction.GASLIMIT, Prepare.EvmCode
                    .GASLIMIT()
                    .Done);

            yield return (Instruction.CALLER, Prepare.EvmCode
                    .CALLER()
                    .Done);

            yield return (Instruction.ADDRESS, Prepare.EvmCode
                    .ADDRESS()
                    .Done);

            yield return (Instruction.ORIGIN, Prepare.EvmCode
                    .ORIGIN()
                    .Done);

            yield return (Instruction.CALLVALUE, Prepare.EvmCode
                    .CALLVALUE()
                    .Done);

            yield return (Instruction.CHAINID, Prepare.EvmCode
                    .CHAINID()
                    .Done);

            yield return (Instruction.GAS, Prepare.EvmCode
                    .GAS()
                    .Done);

            yield return (Instruction.RETURNDATASIZE, Prepare.EvmCode
                    .RETURNDATASIZE()
                    .Done);

            yield return (Instruction.BASEFEE, Prepare.EvmCode
                    .BASEFEE()
                    .Done);

            yield return (Instruction.RETURN, Prepare.EvmCode
                    .RETURN(0, 32)
                    .Done);

            yield return (Instruction.REVERT, Prepare.EvmCode
                    .REVERT(0, 32)
                    .Done);

            yield return (Instruction.CALLDATASIZE, Prepare.EvmCode
                    .CALLDATASIZE()
                    .Done);

            yield return (Instruction.JUMPI, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .JUMPI(9)
                    .PushSingle(3)
                    .JUMPDEST()
                    .PushSingle(0)
                    .MUL()
                    .Done);

            yield return (Instruction.JUMP, Prepare.EvmCode
                    .PushSingle(23)
                    .JUMP(10)
                    .JUMPDEST()
                    .PushSingle(3)
                    .MUL()
                    .STOP()
                    .JUMPDEST()
                    .JUMP(5)
                    .Done);

            yield return (Instruction.SHL, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .SHL()
                    .Done);

            yield return (Instruction.SHR, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .SHR()
                    .Done);

            yield return (Instruction.SAR, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .SAR()
                    .Done);

            yield return (Instruction.AND, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .AND()
                    .Done);

            yield return (Instruction.OR, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .OR()
                    .Done);

            yield return (Instruction.XOR, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .XOR()
                    .Done);

            yield return (Instruction.SLT, Prepare.EvmCode
                    .PushData(23)
                    .PushSingle(4)
                    .SLT()
                    .Done);

            yield return (Instruction.SGT, Prepare.EvmCode
                    .PushData(23)
                    .PushData(1)
                    .SGT()
                    .Done);

            yield return (Instruction.BYTE, Prepare.EvmCode
                    .BYTE(16, UInt256.MaxValue.PaddedBytes(32))
                    .Done);

            yield return (Instruction.JUMP, Prepare.EvmCode
                .JUMP(31)
                .Done);

            yield return (Instruction.LOG0, Prepare.EvmCode
                .Log(0, 0)
                .Done);

            yield return (Instruction.LOG1, Prepare.EvmCode
                .PushData(SampleHexData1.PadLeft(64, '0'))
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Log(1, 0, [TestItem.KeccakA])
                .Done);

            yield return (Instruction.LOG2, Prepare.EvmCode
                .PushData(SampleHexData1.PadLeft(64, '0'))
                .PushData(0)
                .Op(Instruction.MSTORE)
                .PushData(SampleHexData2.PadLeft(64, '0'))
                .PushData(32)
                .Op(Instruction.MSTORE)
                .PushData(SampleHexData1.PadLeft(64, '0'))
                .PushData(64)
                .PushData(SampleHexData2.PadLeft(64, '0'))
                .PushData(96)
                .Op(Instruction.MSTORE)
                .Log(4, 0, [TestItem.KeccakA, TestItem.KeccakB])
                .Done);

            yield return (Instruction.LOG3, Prepare.EvmCode
                .PushData(SampleHexData1.PadLeft(64, '0'))
                .PushData(0)
                .Op(Instruction.MSTORE)
                .PushData(SampleHexData2.PadLeft(64, '0'))
                .PushData(32)
                .Op(Instruction.MSTORE)
                .Log(2, 0, [TestItem.KeccakA, TestItem.KeccakA, TestItem.KeccakB])
                .Done);

            yield return (Instruction.LOG4, Prepare.EvmCode
                .PushData(SampleHexData1.PadLeft(64, '0'))
                .PushData(0)
                .Op(Instruction.MSTORE)
                .PushData(SampleHexData2.PadLeft(64, '0'))
                .PushData(32)
                .Op(Instruction.MSTORE)
                .PushData(SampleHexData1.PadLeft(64, '0'))
                .PushData(64)
                .Op(Instruction.MSTORE)
                .Log(3, 0, [TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakA, TestItem.KeccakB])
                .Done);

            yield return (Instruction.TSTORE | Instruction.TLOAD, Prepare.EvmCode
                .PushData(23)
                .PushData(7)
                .TSTORE()
                .PushData(7)
                .TLOAD()
                .Done);

            yield return (Instruction.SSTORE | Instruction.SLOAD, Prepare.EvmCode
                .PushData(23)
                .PushData(7)
                .SSTORE()
                .PushData(7)
                .SLOAD()
                .Done);

            yield return (Instruction.EXTCODESIZE, Prepare.EvmCode
                .EXTCODESIZE(Address.FromNumber(1))
                .Done);

            yield return (Instruction.EXTCODEHASH, Prepare.EvmCode
                .EXTCODEHASH(Address.FromNumber(1))
                .Done);

            yield return (Instruction.EXTCODECOPY, Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .EXTCODECOPY(Address.FromNumber(1))
                .Done);

            yield return (Instruction.BALANCE, Prepare.EvmCode
                .BALANCE(Address.FromNumber(1))
                .Done);

            yield return (Instruction.SELFBALANCE, Prepare.EvmCode
                .SELFBALANCE()
                .Done);
        }

        [Test, TestCaseSource(nameof(GetBytecodes))]
        public void Ensure_Evm_ILvm_Compatibility((Instruction? opcode, byte[] bytecode) testcase)
        {
            var blkExCtx = new BlockExecutionContext(BuildBlock(MainnetSpecProvider.CancunActivation, SenderRecipientAndMiner.Default).Header);
            var txExCtx = new TxExecutionContext(blkExCtx, TestItem.AddressA, 23, [TestItem.KeccakH.Bytes.ToArray()]);
            var envExCtx = new ExecutionEnvironment(new CodeInfo(testcase.bytecode), Recipient, Sender, Contract, new ReadOnlyMemory<byte>([1, 2, 3, 4, 5, 6, 7]), txExCtx, 23, 7);
            var stack = new byte[1024 * 32];
            var inputBuffer = envExCtx.InputData;
            var returnBuffer = ReadOnlyMemory<byte>.Empty;

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
            var ctx = ILCompiler.CompileSegment("ILEVM_TEST", metadata.Item1, metadata.Item2);
            ctx.PrecompiledSegment(ref iLEvmState, _blockhashProvider, TestState, codeInfoRepository, Prague.Instance, ctx.Data);
            Assert.IsTrue(iLEvmState.EvmException == EvmExceptionType.None);
        }


    }
}
