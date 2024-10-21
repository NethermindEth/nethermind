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
using Nethermind.Core.Collections;
using Nethermind.Specs.Test;

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

            IlAnalyzer.AddPattern<PP>();
            //IlAnalyzer.AddPattern<P01P01>();
            //IlAnalyzer.AddPattern<P01ADD>();
            IlAnalyzer.AddPattern<PJ>();
            IlAnalyzer.AddPattern<S01P>();
            IlAnalyzer.AddPattern<S02P>();
            IlAnalyzer.AddPattern<P01P01SHL>();
            IlAnalyzer.AddPattern<D01P04EQ>();
            IlAnalyzer.AddPattern<D01P04GT>();
            IlAnalyzer.AddPattern<D02MST>();
            IlAnalyzer.AddPattern<P01D02>();
            IlAnalyzer.AddPattern<P01D03>();
            IlAnalyzer.AddPattern<S02S01>();
            IlAnalyzer.AddPattern<P01MLD01S02SUB>();
            IlAnalyzer.AddPattern<P01CDLP01SHRD01P04>();
            IlAnalyzer.AddPattern<P00CDLP01SHRD01P04>();
            IlAnalyzer.AddPattern<D04D02LTIZP02>();
            IlAnalyzer.AddPattern<P20ANDP20ANDD02>();
            IlAnalyzer.AddPattern<GASP01MSTP01CDSLT>();
            IlAnalyzer.AddPattern<GASP01P01MSTCV>();
            IlAnalyzer.AddPattern<MSTP01S01KECSL>();
            IlAnalyzer.AddPattern<D02D02ADDMLD04D03ADDMST>();
            IlAnalyzer.AddPattern<D02S01SHRD03D02MUL>();
            IlAnalyzer.AddPattern<S04S01S04ADDS03P01ADD>();
        }

        public void Execute<T>(byte[] bytecode, T tracer, ForkActivation? fork = null, long gasAvailable = 1_000_000)
            where T : ITxTracer
        {
            Execute<T>(tracer, bytecode, fork, gasAvailable);
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
    [NonParallelizable]
    internal class IlEvmTests : TestBlockChain
    {
        private const string AnalyzerField = "_analyzer";
        private const string PatternField = "_patterns";

        [SetUp]
        public override void Setup()
        {
            base.Setup();

            base.config = new VMConfig()
            {
                IsJitEnabled = true,
                IsPatternMatchingEnabled = true,
                AggressiveJitMode = true,

                PatternMatchingThreshold = 4,
                JittingThreshold = 256,
            };

            CodeInfoRepository.ClearCache();
        }

        public static IEnumerable<(Type, byte[])> GePatBytecodesSamples()
        {

            yield return (null, Prepare.EvmCode
                        .Done);
            yield return (typeof(S04S01S04ADDS03P01ADD), Prepare.EvmCode
                     .PUSHx([5])
                     .PUSHx([4])
                     .PUSHx([3])
                     .PUSHx([2])
                     .PUSHx([1])
                     .SWAPx(4)
                     .SWAPx(1)
                     .SWAPx(4)
                     .ADD()
                     .SWAPx(3)
                     .PUSHx([2])
                     .ADD()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .PushData(0x3)
                     .Op(Instruction.SSTORE)
                     .PushData(0x4)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(D02S01SHRD03D02MUL), Prepare.EvmCode
                     .PUSHx([3])
                     .PUSHx([1])
                     .PUSHx([1])
                     .DUPx(2)
                     .SWAPx(1)
                     .SHR()
                     .DUPx(3)
                     .DUPx(2)
                     .MUL()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .PushData(0x3)
                     .Op(Instruction.SSTORE)
                     .PushData(0x4)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(D02D02ADDMLD04D03ADDMST), Prepare.EvmCode
                     .PUSHx([3])
                     .PUSHx([2])
                     .PUSHx([1])
                     .DUPx(2)
                     .DUPx(2)
                     .ADD()
                     .MLOAD()
                     .DUPx(4)
                     .DUPx(3)
                     .ADD()
                     .MSTORE()
                     .DUPx(3)
                     .DUPx(2)
                     .ADD()
                     .MLOAD()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .PushData(0x3)
                     .Op(Instruction.SSTORE)
                     .PushData(0x4)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(MSTP01S01KECSL), Prepare.EvmCode
                     .PushData(1000)
                     .PUSHx([32]) //length
                     .PUSHx([1]) //location
                     .KECCAK256()
                     .SSTORE()
                     .PUSHx([1]) //location
                     .PushData(100)
                     .PUSHx([20])
                     .MSTORE()
                     .PUSHx([32]) //length
                     .SWAPx(1)
                     .KECCAK256()
                     .SLOAD()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PUSHx([20])
                     .MLOAD()
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(GASP01P01MSTCV), Prepare.EvmCode
                     .GAS()
                     .PUSHx([10])
                     .PUSHx([1])
                     .MSTORE()
                     .CALLVALUE()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .PUSHx([1])
                     .MLOAD()
                     .PushData(0x3)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(GASP01MSTP01CDSLT), Prepare.EvmCode
                     .GAS()
                     .PUSHx([10])
                     .MSTORE()
                     .PUSHx([5])
                     .CALLDATASIZE()
                     .LT()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PUSHx([10])
                     .MLOAD()
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(P20ANDP20ANDD02), Prepare.EvmCode
                     .PushData(1000)
                     .PUSHx([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20])
                     .PUSHx([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 1])
                     .AND()
                     .PUSHx([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 1, 20])
                     .AND()
                     .DUPx(2)
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(D04D02LTIZP02), Prepare.EvmCode
                     .PUSHx([100])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .DUPx(4)
                     .DUPx(2)
                     .LT()
                     .ISZERO()
                     .PUSHx([1, 2])
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(P00CDLP01SHRD01P04), Prepare.EvmCode
                     .PUSHx()
                     .CALLDATALOAD()
                     .PUSHx([1])
                     .SHR()
                     .DUPx(1)
                     .PUSHx([1, 2, 3, 4])
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .PushData(0x3)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(P01CDLP01SHRD01P04), Prepare.EvmCode
                     .PUSHx([10])
                     .CALLDATALOAD()
                     .PUSHx([1])
                     .SHR()
                     .DUPx(1)
                     .PUSHx([1, 2, 3, 4])
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .PushData(0x3)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(P01MLD01S02SUB), Prepare.EvmCode
                     .PushData(300)
                     .PUSHx([1])
                     .MSTORE()
                     .PushData(1000)
                     .PUSHx([1])
                     .MLOAD()
                     .DUPx(1)
                     .SWAPx(2)
                     .SUB()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(D01P04EQ), Prepare.EvmCode
                     .PUSHx([1, 2, 3, 4])
                     .DUPx(1)
                     .PUSHx([1, 2, 3, 4])
                     .EQ()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(D01P04GT), Prepare.EvmCode
                     .PUSHx([1, 2, 3, 4])
                     .DUPx(1)
                     .PUSHx([1, 2, 3, 5])
                     .GT()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(D02MST), Prepare.EvmCode
                     .PUSHx([1])
                     .PUSHx([3])
                     .PUSHx([3])
                     .POP() //to avooid PUSHxDUPx pattern detection
                     .DUPx(2)
                     .MSTORE()
                     .POP()
                     .MLOAD(1)
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(P01D03), Prepare.EvmCode
                     .PUSHx([5])
                     .PUSHx([1])
                     .PUSHx([3])
                     .DUPx(3)
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(P01D02), Prepare.EvmCode
                     .PUSHx([1])
                     .PUSHx([3])
                     .DUPx(2)
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(S02S01), Prepare.EvmCode
                     .PUSHx([5])
                     .PUSHx([1])
                     .PUSHx([3])
                     .SWAPx(2)
                     .SWAPx(1)
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .PushData(0x2)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(S02P), Prepare.EvmCode
                     .PUSHx([5])
                     .PUSHx([1])
                     .PUSHx([3])
                     .SWAPx(2)
                     .POP()
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(S01P), Prepare.EvmCode
                     .PUSHx([2])
                     .PUSHx([3])
                     .SWAPx(1)
                     .POP()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(PJ), Prepare.EvmCode
                    .PUSHx([23])
                    .PUSHx([13])
                    .PUSHx([9])
                    .POP()
                    .JUMP()
                    .JUMPDEST()
                    .PushSingle(3)
                    .MUL()
                    .STOP()
                    .JUMPDEST()
                    .JUMP(8)
                    .Done);
            yield return (typeof(P01P01SHL), Prepare.EvmCode
                     .PUSHx([2])
                     .PUSHx([3])
                     .Op(Instruction.SHL)
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
                     .Done);
            yield return (typeof(PP), Prepare.EvmCode
                     .PushData(((UInt256)3).PaddedBytes(32))
                     .PushData(((UInt256)4).PaddedBytes(32))
                     .PushData(((UInt256)5).PaddedBytes(32))
                     .POP()
                     .POP()
                     .PushData(0x1)
                     .Op(Instruction.SSTORE)
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
            yield return (null, Prepare.EvmCode
                    .Done, EvmExceptionType.None);
            yield return (Instruction.PUSH32, Prepare.EvmCode
                    .PushSingle(1)
                    .Done, EvmExceptionType.None);
            yield return (Instruction.ISZERO, Prepare.EvmCode
                    .ISZERO(7)
                    .ISZERO(0)
                    .ISZERO(7)
                    .Done, EvmExceptionType.None);
            yield return (Instruction.SUB, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .SUB()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.ADD, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.ADDMOD, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .PushSingle(5)
                    .ADDMOD()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.MUL, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .MUL()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.EXP, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .EXP()
                    .PushSingle(0)
                    .PushSingle(7)
                    .EXP()
                    .PushSingle(1)
                    .PushSingle(7)
                    .EXP()
                    .PushSingle(1)
                    .PushSingle(0)
                    .EXP()
                    .PushSingle(1)
                    .PushSingle(1)
                    .EXP()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.MOD, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .MOD()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.DIV, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .DIV()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.MSTORE | Instruction.MLOAD, Prepare.EvmCode
                    .MSTORE(0, ((UInt256)23).PaddedBytes(32))
                    .MLOAD(0)
                    .Done, EvmExceptionType.None);

            yield return (Instruction.MSTORE8, Prepare.EvmCode
                    .MSTORE8(0, ((UInt256)23).PaddedBytes(32))
                    .MLOAD(0)
                    .Done, EvmExceptionType.None);

            yield return (Instruction.MCOPY, Prepare.EvmCode
                    .MSTORE(0, ((UInt256)23).PaddedBytes(32))
                    .MCOPY(32, 0, 32)
                    .Done, EvmExceptionType.None);

            yield return (Instruction.EQ, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .EQ()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.GT, Prepare.EvmCode
                    .PushSingle(7)
                    .PushSingle(23)
                    .GT()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.LT, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .LT()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.NOT, Prepare.EvmCode
                    .PushSingle(1)
                    .NOT()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.BLOBHASH, Prepare.EvmCode
                    .PushSingle(0)
                    .BLOBHASH()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.BLOCKHASH, Prepare.EvmCode
                    .BLOCKHASH(0)
                    .Done, EvmExceptionType.None);

            yield return (Instruction.CALLDATACOPY, Prepare.EvmCode
                    .CALLDATACOPY(0, 0, 32)
                    .Done, EvmExceptionType.None);

            yield return (Instruction.CALLDATALOAD, Prepare.EvmCode
                    .CALLDATALOAD(0)
                    .Done, EvmExceptionType.None);

            yield return (Instruction.MSIZE, Prepare.EvmCode
                    .MSIZE()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.GASPRICE, Prepare.EvmCode
                    .GASPRICE()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.CODESIZE, Prepare.EvmCode
                    .CODESIZE()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.PC, Prepare.EvmCode
                    .PC()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.COINBASE, Prepare.EvmCode
                    .COINBASE()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.TIMESTAMP, Prepare.EvmCode
                    .TIMESTAMP()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.NUMBER, Prepare.EvmCode
                    .NUMBER()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.GASLIMIT, Prepare.EvmCode
                    .GASLIMIT()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.CALLER, Prepare.EvmCode
                    .CALLER()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.ADDRESS, Prepare.EvmCode
                    .ADDRESS()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.ORIGIN, Prepare.EvmCode
                    .ORIGIN()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.CALLVALUE, Prepare.EvmCode
                    .CALLVALUE()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.CHAINID, Prepare.EvmCode
                    .CHAINID()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.GAS, Prepare.EvmCode
                    .PushData(23)
                    .PushData(46)
                    .ADD()
                    .POP()
                    .GAS()
                    .PushData(23)
                    .PushData(46)
                    .ADD()
                    .POP()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.RETURNDATASIZE, Prepare.EvmCode
                    .RETURNDATASIZE()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.BASEFEE, Prepare.EvmCode
                    .BASEFEE()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.RETURN, Prepare.EvmCode
                    .StoreDataInMemory(0, [2, 3, 5, 7])
                    .RETURN(0, 32)
                    .Done, EvmExceptionType.None);

            yield return (Instruction.REVERT, Prepare.EvmCode
                    .StoreDataInMemory(0, [2, 3, 5, 7])
                    .REVERT(0, 32)
                    .Done, EvmExceptionType.None);

            yield return (Instruction.CALLDATASIZE, Prepare.EvmCode
                    .CALLDATASIZE()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.JUMPI | Instruction.JUMPDEST, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .JUMPI(9)
                    .PushSingle(3)
                    .JUMPDEST()
                    .PushSingle(0)
                    .MUL()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.JUMP | Instruction.JUMPDEST, Prepare.EvmCode
                    .PushSingle(23)
                    .JUMP(10)
                    .JUMPDEST()
                    .PushSingle(3)
                    .MUL()
                    .STOP()
                    .JUMPDEST()
                    .JUMP(5)
                    .Done, EvmExceptionType.None);

            yield return (Instruction.SHL, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .SHL()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.SHR, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .SHR()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.SAR, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .SAR()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.AND, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .AND()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.OR, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .OR()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.XOR, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .XOR()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.SLT, Prepare.EvmCode
                    .PushData(23)
                    .PushSingle(4)
                    .SLT()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.SGT, Prepare.EvmCode
                    .PushData(23)
                    .PushData(1)
                    .SGT()
                    .Done, EvmExceptionType.None);

            yield return (Instruction.BYTE, Prepare.EvmCode
                    .BYTE(16, UInt256.MaxValue.PaddedBytes(32))
                    .Done, EvmExceptionType.None);

            yield return (Instruction.JUMP | Instruction.JUMPDEST, Prepare.EvmCode
                .JUMP(31)
                .Done, EvmExceptionType.None);

            yield return (Instruction.LOG0, Prepare.EvmCode
                .Log(0, 0)
                .Done, EvmExceptionType.None);

            yield return (Instruction.LOG1, Prepare.EvmCode
                .PushData(SampleHexData1.PadLeft(64, '0'))
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Log(1, 0, [TestItem.KeccakA])
                .Done, EvmExceptionType.None);

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
                .Done, EvmExceptionType.None);

            yield return (Instruction.LOG3, Prepare.EvmCode
                .PushData(SampleHexData1.PadLeft(64, '0'))
                .PushData(0)
                .Op(Instruction.MSTORE)
                .PushData(SampleHexData2.PadLeft(64, '0'))
                .PushData(32)
                .Op(Instruction.MSTORE)
                .Log(2, 0, [TestItem.KeccakA, TestItem.KeccakA, TestItem.KeccakB])
                .Done, EvmExceptionType.None);

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
                .Done, EvmExceptionType.None);

            yield return (Instruction.TSTORE | Instruction.TLOAD, Prepare.EvmCode
                .PushData(23)
                .PushData(7)
                .TSTORE()
                .PushData(7)
                .TLOAD()
                .Done, EvmExceptionType.None);

            yield return (Instruction.SSTORE | Instruction.SLOAD, Prepare.EvmCode
                .PushData(23)
                .PushData(7)
                .SSTORE()
                .PushData(7)
                .SLOAD()
                .Done, EvmExceptionType.None);

            yield return (Instruction.EXTCODESIZE, Prepare.EvmCode
                .EXTCODESIZE(Address.FromNumber(1))
                .Done, EvmExceptionType.None);

            yield return (Instruction.EXTCODEHASH, Prepare.EvmCode
                .EXTCODEHASH(Address.FromNumber(1))
                .Done, EvmExceptionType.None);

            yield return (Instruction.EXTCODECOPY, Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .EXTCODECOPY(Address.FromNumber(1))
                .Done, EvmExceptionType.None);

            yield return (Instruction.BALANCE, Prepare.EvmCode
                .BALANCE(Address.FromNumber(1))
                .Done, EvmExceptionType.None);

            yield return (Instruction.SELFBALANCE, Prepare.EvmCode
                .SELFBALANCE()
                .Done, EvmExceptionType.None);

            yield return (Instruction.INVALID, Prepare.EvmCode
                .INVALID()
                .Done, EvmExceptionType.BadInstruction);

            yield return (Instruction.STOP, Prepare.EvmCode
                .STOP()
                .Done, EvmExceptionType.None);

            yield return (Instruction.POP, Prepare.EvmCode
                .PUSHx()
                .POP()
                .Done, EvmExceptionType.None);

            for (byte opcode = (byte)Instruction.DUP1; opcode <= (byte)Instruction.DUP16; opcode++)
            {
                int n = opcode - (byte)Instruction.DUP1 + 1;
                var test = Prepare.EvmCode;
                for (int i = 0; i < n; i++)
                {
                    test.PushData(i);
                }
                test.Op((Instruction)opcode);

                yield return ((Instruction)opcode, test.Done, EvmExceptionType.None);
            }

            for (byte opcode = (byte)Instruction.PUSH0; opcode <= (byte)Instruction.PUSH32; opcode++)
            {
                int n = opcode - (byte)Instruction.PUSH0;
                byte[] args = n == 0 ? null : Enumerable.Range(0, n).Select(i => (byte)i).ToArray();

                yield return ((Instruction)opcode, Prepare.EvmCode.PUSHx(args).Done, EvmExceptionType.None);
            }

            for (byte opcode = (byte)Instruction.SWAP1; opcode <= (byte)Instruction.SWAP16; opcode++)
            {
                int n = opcode - (byte)Instruction.SWAP1 + 2;
                var test = Prepare.EvmCode;
                for (int i = 0; i < n; i++)
                {
                    test.PushData(i);
                }
                test.Op((Instruction)opcode);

                yield return ((Instruction)opcode, test.Done, EvmExceptionType.None);
            }

            yield return (Instruction.SDIV, Prepare.EvmCode
                .PushData(23)
                .PushData(7)
                .SDIV()
                .Done, EvmExceptionType.None);

            yield return (Instruction.SMOD, Prepare.EvmCode
                .PushData(23)
                .PushData(7)
                .SMOD()
                .Done, EvmExceptionType.None);

            yield return (Instruction.CODECOPY, Prepare.EvmCode
                .PushData(0)
                .PushData(32)
                .PushData(7)
                .CODECOPY()
                .Done, EvmExceptionType.None);

            yield return (Instruction.MULMOD, Prepare.EvmCode
                .PushData(23)
                .PushData(3)
                .PushData(7)
                .MULMOD()
                .Done, EvmExceptionType.None);

            yield return (Instruction.KECCAK256, Prepare.EvmCode
                .MSTORE(0, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray())
                .PushData(0)
                .PushData(16)
                .KECCAK256()
                .Done, EvmExceptionType.None);

            yield return (Instruction.PREVRANDAO, Prepare.EvmCode
                .PREVRANDAO()
                .Done, EvmExceptionType.None);

            yield return (Instruction.RETURNDATACOPY, Prepare.EvmCode
                .PushData(0)
                .PushData(32)
                .PushData(0)
                .RETURNDATACOPY()
                .Done, EvmExceptionType.None);

            yield return (Instruction.BLOBBASEFEE, Prepare.EvmCode
                .Op(Instruction.BLOBBASEFEE)
                .Done, EvmExceptionType.None);

            yield return (Instruction.SIGNEXTEND, Prepare.EvmCode
                .PushData(1024)
                .PushData(16)
                .SIGNEXTEND()
                .Done, EvmExceptionType.None);

            yield return (Instruction.INVALID, Prepare.EvmCode
                .JUMPDEST()
                .MUL(23, 3)
                .POP()
                .JUMP(0)
                .Done, EvmExceptionType.OutOfGas);

            yield return (Instruction.INVALID, Prepare.EvmCode
                .JUMPDEST()
                .PUSHx()
                .DUPx(1)
                .DUPx(1)
                .DUPx(1)
                .DUPx(1)
                .DUPx(1)
                .DUPx(1)
                .DUPx(1)
                .JUMP(0)
                .Done, EvmExceptionType.StackOverflow);

            yield return (Instruction.INVALID, Prepare.EvmCode
                .JUMPDEST()
                .MUL(23)
                .JUMP(0)
                .Done, EvmExceptionType.StackUnderflow);
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

            CodeInfo codeInfo = new CodeInfo(bytecode, TestItem.AddressA);

            await IlAnalyzer.StartAnalysis(codeInfo, ILMode.PAT_MODE, NullLogger.Instance, config);

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

            CodeInfo codeInfo = new CodeInfo(bytecode, TestItem.AddressA);

            await IlAnalyzer.StartAnalysis(codeInfo, IlInfo.ILMode.JIT_MODE, NullLogger.Instance, config);

            codeInfo.IlInfo.Segments.Count.Should().Be(2);
        }

        [Test]
        public void Execution_Swap_Happens_When_Pattern_Occurs()
        {
            int repeatCount = 128;

            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                PatternMatchingThreshold = repeatCount + 1,
                IsPatternMatchingEnabled = true,
                JittingThreshold = int.MaxValue,
                IsJitEnabled = false,
                AggressiveJitMode = false
            });

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
            for (int i = 0; i < repeatCount * 2; i++)
            {
                var tracer = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
                enhancedChain.Execute<GethLikeTxMemoryTracer>(bytecode, tracer);
                var traces = tracer.BuildResult().Entries.Where(tr => tr.IsPrecompiled is not null && !tr.IsPrecompiled.Value).ToList();
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
                .Call(address, 100)
                .POP()
                .PushData(1000)
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
            int repeatCount = 10;

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
                    .PushData(UInt256.MaxValue)
                    .PUSHx([2])
                    .MSTORE()
                    .PushData(0)
                    .PushData(0)
                    .PushData(32) // length
                    .PushData(2) // offset
                    .PushData(0) // value
                    .PushData(address)
                    .PushData(250000)
                    .Op(Instruction.CALL)
                    .POP()
                    .GAS()
                    .PushData(1000)
                    .LT()
                    .JUMPI(0)
                    .STOP()
                    .Done;


            var fork = (ForkActivation)MainnetSpecProvider.CancunActivation;

            for (var i = 0; i < repeatCount * 2; i++)
            {
                standardChain.Execute<GethLikeTxMemoryTracer>(bytecode, tracer1, fork);
            }

            for (var i = 0; i < repeatCount * 2; i++)
            {
                enhancedChain.Execute<GethLikeTxMemoryTracer>(bytecode, tracer2, fork);
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
        public void JIT_Mode_Segment_Has_Jump_Into_Another_Segment_Agressive_Mode_On()
        {
            int repeatCount = 32;

            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                PatternMatchingThreshold = repeatCount + 1,
                IsPatternMatchingEnabled = false,
                JittingThreshold = repeatCount + 1,
                IsJitEnabled = true,
                AggressiveJitMode = true
            });

            var address = enhancedChain.InsertCode(Prepare.EvmCode
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
                    .JUMPI(59)
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .POP()
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
            for (int i = 0; i <= repeatCount * 32; i++)
            {
                var tracer = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
                enhancedChain.Execute(bytecode, tracer);
                var traces = tracer.BuildResult().Entries.Where(tr => tr.SegmentID is not null).ToList();
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
                "ILEVM_PRECOMPILED_(0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358)[0..47]",
                "ILEVM_PRECOMPILED_(0x4df55fd3a4afecd4a0b4550f05b7cca2aa1db9a1)[0..5]",
                "ILEVM_PRECOMPILED_(0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358)[49..60]",
                "ILEVM_PRECOMPILED_(0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358)[0..47]",
                "ILEVM_PRECOMPILED_(0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358)[49..60]",
            };

            string[] actualTracePattern = accumulatedTraces.TakeLast(5).Select(tr => tr.SegmentID).ToArray();
            Assert.That(actualTracePattern, Is.EqualTo(desiredTracePattern));
        }


        [Test]
        public void JIT_Mode_Segment_Has_Jump_Into_Another_Segment_Agressive_Mode_Off()
        {
            int repeatCount = 32;

            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                PatternMatchingThreshold = repeatCount * 2 + 1,
                IsPatternMatchingEnabled = true,
                JittingThreshold = repeatCount + 1,
                IsJitEnabled = true,
                AggressiveJitMode = false
            });

            var address = enhancedChain.InsertCode(Prepare.EvmCode
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
                    .JUMPI(59)
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .POP()
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
            for (int i = 0; i <= repeatCount * 2; i++)
            {
                var tracer = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
                enhancedChain.Execute(bytecode, tracer);
                var traces = tracer.BuildResult().Entries.Where(tr => tr.SegmentID is not null).ToList();
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
                "ILEVM_PRECOMPILED_(0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358)[0..47]",
                "ILEVM_PRECOMPILED_(0x4df55fd3a4afecd4a0b4550f05b7cca2aa1db9a1)[0..5]",
                "ILEVM_PRECOMPILED_(0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358)[49..60]",
                "ILEVM_PRECOMPILED_(0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358)[0..47]",
                "AbortDestinationPattern",
            };

            string[] actualTracePattern = accumulatedTraces.TakeLast(5).Select(tr => tr.SegmentID).ToArray();
            Assert.That(actualTracePattern, Is.EqualTo(desiredTracePattern));
        }


        [Test]
        public void JIT_invalid_opcode_results_in_failure()
        {
            int repeatCount = 32;

            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                PatternMatchingThreshold = repeatCount + 1,
                IsPatternMatchingEnabled = true,
                JittingThreshold = int.MaxValue,
                IsJitEnabled = true,
                AggressiveJitMode = false
            });

            byte[] bytecode =
                Prepare.EvmCode
                    .PUSHx() // PUSH0
                    .POP()
                    .STOP()
                    .Done;

            var accumulatedTraces = new List<bool>();
            var numberOfRuns = config.JittingThreshold * 1024;
            for (int i = 0; i < numberOfRuns; i++)
            {
                var tracer = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
                enhancedChain.Execute(bytecode, tracer);
                var traces = tracer.BuildResult();
                accumulatedTraces.AddRange(traces.Failed);
            }

            Assert.That(accumulatedTraces.All(status => status), Is.True);
        }

        [Test]
        public void Execution_Swap_Happens_When_Segments_are_compiled()
        {
            int repeatCount = 32;

            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                PatternMatchingThreshold = int.MaxValue,
                IsPatternMatchingEnabled = false,
                JittingThreshold = repeatCount + 1,
                IsJitEnabled = true,
                AggressiveJitMode = false
            });

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
            for (int i = 0; i <= repeatCount * 32; i++)
            {
                var tracer = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
                enhancedChain.Execute(bytecode, tracer);
                var traces = tracer.BuildResult().Entries.Where(tr => tr.IsPrecompiled is not null && tr.IsPrecompiled.Value).ToList();
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
            var codeInfo = new CodeInfo(testcase.bytecode, TestItem.AddressA);
            var blkExCtx = new BlockExecutionContext(BuildBlock(MainnetSpecProvider.CancunActivation, SenderRecipientAndMiner.Default).Header);
            var txExCtx = new TxExecutionContext(blkExCtx, TestItem.AddressA, 23, [TestItem.KeccakH.Bytes.ToArray()], CodeInfoRepository);
            var envExCtx = new ExecutionEnvironment(codeInfo, Recipient, Sender, Contract, new ReadOnlyMemory<byte>([1, 2, 3, 4, 5, 6, 7]), txExCtx, 23, 7);
            var stack = new byte[1024 * 32];
            var inputBuffer = envExCtx.InputData;
            var returnBuffer =
                new ReadOnlyMemory<byte>(Enumerable.Range(0, 32)
                .Select(i => (byte)i).ToArray());

            TestState.CreateAccount(Address.FromNumber(1), 1000000);
            TestState.InsertCode(Address.FromNumber(1), testcase.bytecode, Prague.Instance);

            var state = new EvmState(
                1_000_000,
                new ExecutionEnvironment(codeInfo, Address.FromNumber(1), Address.FromNumber(1), Address.FromNumber(1), ReadOnlyMemory<byte>.Empty, txExCtx, 0, 0),
                ExecutionType.CALL,
                isTopLevel: false,
                Snapshot.Empty,
                isContinuation: false);

            IVirtualMachine evm = typeof(VirtualMachine).GetField("_evm", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Machine) as IVirtualMachine;

            state.InitStacks();

            ILEvmState iLEvmState = new ILEvmState(SpecProvider.ChainId, state, EvmExceptionType.None, 0, 100000, ref returnBuffer);
            var metadata = IlAnalyzer.StripByteCode(testcase.bytecode);
            var ctx = ILCompiler.CompileSegment("ILEVM_TEST", metadata.Item1, metadata.Item2);
            ctx.PrecompiledSegment(ref iLEvmState, _blockhashProvider, TestState, CodeInfoRepository, Prague.Instance, ctx.Data);

            Assert.That(iLEvmState.EvmException == testcase.exceptionType);
        }
    }
}
