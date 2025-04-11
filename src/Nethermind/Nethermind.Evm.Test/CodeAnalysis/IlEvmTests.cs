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
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Core.Crypto;
using static Nethermind.Evm.CodeAnalysis.IL.IlInfo;
using Nethermind.Db;
using Nethermind.Trie.Pruning;
using System.Diagnostics;
using Nethermind.Abi;
using System.Runtime.CompilerServices;
using Nethermind.Trie;

using static System.Runtime.CompilerServices.Unsafe;
using System.Reflection.Emit;
using System.IO;
using System.Runtime.Loader;
namespace Nethermind.Evm.Test.CodeAnalysis
{
    internal class TestBlockChain : VirtualMachineTestsBase
    {
        protected IVMConfig config;
        public TestBlockChain(IVMConfig config, IReleaseSpec spec)
        {
            this.config = config;
            SpecProvider = new TestSpecProvider(spec);
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

            AotContractsRepository.ClearCache();

            IlAnalyzer.StartPrecompilerBackgroundThread(config, NullLogger.Instance);

            Metrics.IlvmAotPrecompiledCalls = 0;

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
            InsertCode(code, Address.FromNumber((int)Instruction.RETURN));

            var returningCode = Prepare.EvmCode
                        .PushData(UInt256.MaxValue)
                        .PUSHx([0])
                        .MSTORE()
                        .Return(32, 0)
                        .STOP()
                        .Done;
            InsertCode(returningCode, Address.FromNumber((int)Instruction.RETURNDATASIZE));
        }

        public void Execute<T>(byte[] bytecode, T tracer, ForkActivation? fork = null, long gasAvailable = 10_000_000, byte[][] blobVersionedHashes  = null)
            where T : ITxTracer
        {
            Execute<T>(tracer, bytecode, fork ?? MainnetSpecProvider.PragueActivation, gasAvailable, blobVersionedHashes);
        }

        public Address InsertCode(byte[] bytecode, Address? target = null)
        {
            var hashcode = Keccak.Compute(bytecode);
            var address = target ?? new Address(hashcode);

            TestState.CreateAccount(address, 1_000_000_000);
            TestState.InsertCode(address, bytecode, Spec);
            return address;
        }
        public void ForceRunAnalysis(Address address, int mode)
        {
            var codeinfo = CodeInfoRepository.GetCachedCodeInfo(TestState, address, Prague.Instance, out _);

            if (mode.HasFlag(ILMode.FULL_AOT_MODE))
            {
                IlAnalyzer.Analyse(codeinfo, ILMode.FULL_AOT_MODE, config, NullLogger.Instance);
            }

            codeinfo.IlInfo.AnalysisPhase = AnalysisPhase.Completed;
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
    internal class IlEvmTests
    {
        private const string PatternField = "_patterns";
        private const int RepeatCount = 256;
        public static IEnumerable<(string, Instruction[], byte[], EvmExceptionType, bool, IReleaseSpec)> GetJitBytecodesSamples()
        {
            IEnumerable<(Instruction[], byte[], EvmExceptionType, bool)> GetJitBytecodesSamplesGenerator(bool turnOnAggressiveMode)
            {
                yield return ([Instruction.PUSH32], Prepare.EvmCode
                        .PUSHx([1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1])
                        .PushSingle(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ISZERO], Prepare.EvmCode
                        .ISZERO(7)
                        .PushData(7)
                        .SSTORE()
                        .ISZERO(0)
                        .PushData(1)
                        .SSTORE()
                        .ISZERO(UInt256.MaxValue)
                        .PushData(23)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SUB], Prepare.EvmCode
                        .PushSingle(UInt256.MaxValue)
                        .PushSingle(7)
                        .SUB()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SUB], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .SUB()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SUB], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(0)
                        .SUB()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SUB], Prepare.EvmCode
                        .PushSingle(0)
                        .PushSingle(7)
                        .SUB()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADD], Prepare.EvmCode
                        .PushSingle(UInt256.MaxValue)
                        .PushSingle(UInt256.MaxValue)
                        .ADD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.ADD], Prepare.EvmCode
                        .PushSingle(UInt256.MaxValue)
                        .PushSingle(7)
                        .ADD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.ADD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .ADD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .PushSingle(5)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(0)
                        .PushSingle(7)
                        .PushSingle(5)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(1)
                        .PushSingle(7)
                        .PushSingle(5)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(0)
                        .PushSingle(5)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(1)
                        .PushSingle(5)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .PushSingle(0)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .PushSingle(1)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .PushSingle(23)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(7)
                        .PushSingle(7)
                        .PushSingle(23)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MUL], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .MUL()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MUL], Prepare.EvmCode
                        .PushSingle(UInt256.MaxValue / 2)
                        .PushSingle(2)
                        .MUL()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MUL], Prepare.EvmCode
                        .PushSingle(UInt256.MaxValue)
                        .PushSingle(2)
                        .MUL()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MUL], Prepare.EvmCode
                        .PushSingle(0)
                        .PushSingle(7)
                        .MUL()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MUL], Prepare.EvmCode
                        .PushSingle(1)
                        .PushSingle(7)
                        .MUL()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MUL], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(1)
                        .MUL()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MUL], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(0)
                        .MUL()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXP], Prepare.EvmCode
                        .PushSingle(2)
                        .PushSingle(255)
                        .EXP()
                        .PushData(2)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.EXP], Prepare.EvmCode
                        .PushSingle(0)
                        .PushSingle(7)
                        .EXP()
                        .PushData(2)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXP], Prepare.EvmCode
                        .PushSingle(1)
                        .PushSingle(7)
                        .EXP()
                        .PushData(3)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXP], Prepare.EvmCode
                        .PushSingle(1)
                        .PushSingle(0)
                        .EXP()
                        .PushData(4)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXP], Prepare.EvmCode
                        .PushSingle(1)
                        .PushSingle(1)
                        .EXP()
                        .PushData(5)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .MOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MOD], Prepare.EvmCode
                        .PushSingle(1)
                        .PushSingle(7)
                        .MOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(1)
                        .MOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(0)
                        .MOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MOD], Prepare.EvmCode
                        .PushSingle(0)
                        .PushSingle(7)
                        .MOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.DIV], Prepare.EvmCode
                        .PushSingle(1)
                        .PushSingle(0)
                        .DIV()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.DIV], Prepare.EvmCode
                        .PushSingle(0)
                        .PushSingle(1)
                        .DIV()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.DIV], Prepare.EvmCode
                        .PushSingle(UInt256.MaxValue)
                        .PushSingle(UInt256.MaxValue)
                        .PushSingle(100000)
                        .DIV()
                        .DIV()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.DIV], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .DIV()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE, Instruction.MLOAD], Prepare.EvmCode
                        .MSTORE(0, ((UInt256)23).PaddedBytes(32))
                        .MLOAD(0)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE, Instruction.MLOAD], Prepare.EvmCode
                        .MSTORE(123, ((UInt256)23).PaddedBytes(32))
                        .MLOAD(0)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE, Instruction.MLOAD], Prepare.EvmCode
                        .MSTORE(32, ((UInt256)23).PaddedBytes(32))
                        .MLOAD(0)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE, Instruction.MLOAD], Prepare.EvmCode
                        .MSTORE(0, ((UInt256)0).PaddedBytes(32))
                        .MLOAD(0)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE, Instruction.MLOAD], Prepare.EvmCode
                        .MSTORE(123, ((UInt256)0).PaddedBytes(32))
                        .MLOAD(123)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE, Instruction.MLOAD], Prepare.EvmCode
                        .MSTORE(32, ((UInt256)0).PaddedBytes(32))
                        .MLOAD(32)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE8], Prepare.EvmCode
                        .MSTORE8(0, ((UInt256)23).PaddedBytes(32))
                        .MLOAD(0)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE8], Prepare.EvmCode
                        .MSTORE8(123, ((UInt256)23).PaddedBytes(32))
                        .MLOAD(123)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE8], Prepare.EvmCode
                        .MSTORE8(32, ((UInt256)23).PaddedBytes(32))
                        .MLOAD(32)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE8], Prepare.EvmCode
                        .MSTORE8(0, UInt256.MaxValue.PaddedBytes(32))
                        .MLOAD(0)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE8], Prepare.EvmCode
                        .MSTORE8(123, UInt256.MaxValue.PaddedBytes(32))
                        .MLOAD(123)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE8], Prepare.EvmCode
                        .MSTORE8(32, UInt256.MaxValue.PaddedBytes(32))
                        .MLOAD(32)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MCOPY], Prepare.EvmCode
                        .MSTORE(0, ((UInt256)23).PaddedBytes(32))
                        .MCOPY(32, 0, 32)
                        .MLOAD(32)
                        .MLOAD(0)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MCOPY], Prepare.EvmCode
                        .MSTORE(123, ((UInt256)23).PaddedBytes(32))
                        .MCOPY(32, 123, 32)
                        .MLOAD(32)
                        .MLOAD(0)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MCOPY], Prepare.EvmCode
                        .MSTORE(32, ((UInt256)23).PaddedBytes(32))
                        .MCOPY(32, 123, 32)
                        .MLOAD(32)
                        .MLOAD(0)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MCOPY], Prepare.EvmCode
                        .MSTORE(0, ((UInt256)0).PaddedBytes(32))
                        .MCOPY(32, 0, 32)
                        .MLOAD(32)
                        .MLOAD(0)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MCOPY], Prepare.EvmCode
                        .MSTORE(123, ((UInt256)0).PaddedBytes(32))
                        .MCOPY(32, 123, 32)
                        .MLOAD(32)
                        .MLOAD(123)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MCOPY], Prepare.EvmCode
                        .MSTORE(32, ((UInt256)0).PaddedBytes(32))
                        .MCOPY(0, 32, 32)
                        .MLOAD(32)
                        .MLOAD(0)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MCOPY], Prepare.EvmCode
                        .MSTORE(32, ((UInt256)0).PaddedBytes(32))
                        .MCOPY(32, 32, 32)
                        .MLOAD(32)
                        .PushSingle((UInt256)0)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MCOPY], Prepare.EvmCode
                        .MSTORE(32, ((UInt256)23).PaddedBytes(32))
                        .MCOPY(32, 32, 32)
                        .MLOAD(32)
                        .PushSingle((UInt256)23)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EQ], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(23)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.EQ], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.GT], Prepare.EvmCode
                        .PushSingle(7)
                        .PushSingle(23)
                        .GT()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.GT], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .GT()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.GT], Prepare.EvmCode
                        .PushSingle(17)
                        .PushSingle(17)
                        .GT()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.LT], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .LT()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.LT], Prepare.EvmCode
                        .PushSingle(7)
                        .PushSingle(23)
                        .LT()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.LT], Prepare.EvmCode
                        .PushSingle(17)
                        .PushSingle(17)
                        .LT()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.NOT], Prepare.EvmCode
                        .PushSingle(1)
                        .NOT()
                        .PushData(1)
                        .SSTORE()
                        .PushSingle(0)
                        .NOT()
                        .PushData(2)
                        .SSTORE()
                        .PushSingle(1024)
                        .NOT()
                        .PushData(3)
                        .SSTORE()
                        .PushSingle(UInt256.MaxValue)
                        .NOT()
                        .PushData(4)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BLOBHASH], Prepare.EvmCode
                        .PushSingle(11)
                        .BLOBHASH()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BLOBHASH], Prepare.EvmCode
                        .PushSingle(9)
                        .BLOBHASH()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BLOBHASH], Prepare.EvmCode
                        .PushSingle(0)
                        .BLOBHASH()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BLOCKHASH], Prepare.EvmCode
                    .BLOCKHASH(UInt256.MaxValue - 1000)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BLOCKHASH], Prepare.EvmCode
                    .BLOCKHASH(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CALLDATACOPY], Prepare.EvmCode
                    .CALLDATACOPY(2, 2, 10) //dest, src, len
                    .MLOAD(2)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.CALLDATACOPY], Prepare.EvmCode
                    .CALLDATACOPY(0, 30, 2)
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.CALLDATACOPY], Prepare.EvmCode
                    .CALLDATACOPY(0, 0, 32)
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CALLDATALOAD], Prepare.EvmCode
                    .CALLDATALOAD(16)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CALLDATALOAD], Prepare.EvmCode
                    .CALLDATALOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSIZE], Prepare.EvmCode
                    .PushData(TestBlockChain.SampleHexData1.PadLeft(64, '0'))
                    .PushData(0)
                    .Op(Instruction.MSTORE)
                    .MSIZE()
                    .PushData(1)
                    .SSTORE()
                    .PushData(TestBlockChain.SampleHexData1.PadLeft(64, '0'))
                    .PushData(32)
                    .Op(Instruction.MSTORE)
                    .MSIZE()
                    .PushData(2)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.MSIZE], Prepare.EvmCode
                    .MSIZE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.GASPRICE], Prepare.EvmCode
                    .GASPRICE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CODESIZE], Prepare.EvmCode
                    .CODESIZE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.PC], Prepare.EvmCode
                    .PC()
                    .PushData(1)
                    .SSTORE()
                    .PC()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.COINBASE], Prepare.EvmCode
                    .COINBASE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.TIMESTAMP], Prepare.EvmCode
                    .TIMESTAMP()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.NUMBER], Prepare.EvmCode
                    .NUMBER()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.GASLIMIT], Prepare.EvmCode
                    .GASLIMIT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CALLER], Prepare.EvmCode
                    .CALLER()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDRESS], Prepare.EvmCode
                    .ADDRESS()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ORIGIN], Prepare.EvmCode
                    .ORIGIN()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CALLVALUE], Prepare.EvmCode
                    .CALLVALUE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CHAINID], Prepare.EvmCode
                    .CHAINID()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.GAS], Prepare.EvmCode
                    .PushData(23)
                    .PushData(46)
                    .ADD()
                    .POP()
                    .GAS()
                    .PushData(1)
                    .SSTORE()
                    .PushData(23)
                    .PushData(46)
                    .ADD()
                    .POP()
                    .GAS()
                    .PushData(2)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.RETURNDATASIZE], Prepare.EvmCode
                    .RETURNDATASIZE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BASEFEE], Prepare.EvmCode
                    .BASEFEE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.RETURN], Prepare.EvmCode
                    .StoreDataInMemory(0, [2, 3, 5, 7])
                    .RETURN(0, 32)
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE().Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.REVERT], Prepare.EvmCode
                    .StoreDataInMemory(0, [2, 3, 5, 7])
                    .REVERT(0, 32)
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CALLDATASIZE], Prepare.EvmCode
                    .CALLDATASIZE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.JUMPI, Instruction.JUMPDEST], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .JUMPI(9)
                    .PushSingle(3)
                    .JUMPDEST()
                    .PushSingle(0)
                    .MUL()
                    .GAS()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);


                yield return ([Instruction.JUMPI, Instruction.JUMPDEST], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(0)
                    .JUMPI(9)
                    .PushSingle(3)
                    .JUMPDEST()
                    .PushSingle(0)
                    .MUL()
                    .GAS()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.JUMP, Instruction.JUMPDEST], Prepare.EvmCode
                    .PushSingle(23)
                    .JUMP(14)
                    .JUMPDEST()
                    .PushSingle(3)
                    .MUL()
                    .GAS()
                    .PUSHx([1])
                    .SSTORE()
                    .STOP()
                    .JUMPDEST()
                    .JUMP(5)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.JUMPDEST], Prepare.EvmCode
                    .JUMPDEST()
                    .PushSingle(3)
                    .PushSingle(3)
                    .MUL()
                    .GAS()
                    .PUSHx([1])
                    .SSTORE()
                    .STOP()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);



                yield return ([Instruction.SHL], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(0)
                    .SHL()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SHL], Prepare.EvmCode
                    .PushSingle(255)
                    .PushSingle(10)
                    .SHL()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SHL], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .SHL()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SHL], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(32)
                    .SHL()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SHR], Prepare.EvmCode
                    .PushSingle(UInt256.MaxValue)
                    .PushSingle(255)
                    .SHR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SHR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(256)
                    .SHR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SHR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .SHR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SHR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(32)
                    .SHR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SAR], Prepare.EvmCode
                    .PushSingle(UInt256.MaxValue - 2)
                    .PushSingle(0)
                    .SAR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SAR], Prepare.EvmCode
                    .PushSingle(UInt256.MaxValue - 2)
                    .PushSingle(1)
                    .SAR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SAR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(0)
                    .SAR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SAR], Prepare.EvmCode
                    .PushSingle(0)
                    .PushSingle(23)
                    .SAR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SAR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(17)
                    .SAR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SAR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle((UInt256)((Int256.Int256)(-1)))
                    .SAR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SAR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle((UInt256)((Int256.Int256)(-1)))
                    .SAR()
                    .PushSingle((UInt256)((Int256.Int256)(1)))
                    .SAR()
                    .PushSingle(23)
                    .EQ()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.AND], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .AND()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.AND], Prepare.EvmCode
                    .PushSingle(0)
                    .PushSingle(UInt256.MaxValue)
                    .AND()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.AND], Prepare.EvmCode
                    .PushSingle(UInt256.MaxValue)
                    .PushSingle(0)
                    .AND()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.OR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .OR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.OR], Prepare.EvmCode
                    .PushSingle(0)
                    .PushSingle(UInt256.MaxValue)
                    .OR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.OR], Prepare.EvmCode
                    .PushSingle(UInt256.MaxValue)
                    .PushSingle(0)
                    .OR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.XOR], Prepare.EvmCode
                    .PushSingle(UInt256.MaxValue)
                    .PushSingle(1023)
                    .XOR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.XOR], Prepare.EvmCode
                    .PushSingle(255)
                    .PushSingle(3)
                    .XOR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.XOR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .XOR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SLT], Prepare.EvmCode
                    .PushData(UInt256.MaxValue - 1)
                    .PushSingle(UInt256.MaxValue - 2)
                    .SLT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SLT], Prepare.EvmCode
                    .PushSingle(UInt256.MaxValue - 2)
                    .PushData(UInt256.MaxValue - 1)
                    .SLT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SLT], Prepare.EvmCode
                    .PushSingle(17)
                    .PushData(23)
                    .SLT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SLT], Prepare.EvmCode
                    .PushData(23)
                    .PushSingle(17)
                    .SLT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SLT], Prepare.EvmCode
                    .PushData(17)
                    .PushSingle(17)
                    .SLT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SGT], Prepare.EvmCode
                    .PushData(23)
                    .PushData(17)
                    .SGT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SGT], Prepare.EvmCode
                    .PushData(UInt256.MaxValue - 1)
                    .PushSingle(UInt256.MaxValue - 2)
                    .SLT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SGT], Prepare.EvmCode
                    .PushSingle(UInt256.MaxValue - 2)
                    .PushData(UInt256.MaxValue - 1)
                    .SLT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SGT], Prepare.EvmCode
                    .PushData(17)
                    .PushData(17)
                    .SGT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SGT], Prepare.EvmCode
                    .PushData(17)
                    .PushData(23)
                    .SGT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BYTE], Prepare.EvmCode
                    .BYTE(0, ((UInt256)(23)).PaddedBytes(32))
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BYTE], Prepare.EvmCode
                    .BYTE(31, UInt256.MaxValue.PaddedBytes(32))
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.BYTE], Prepare.EvmCode
                    .BYTE(0, UInt256.MaxValue.PaddedBytes(32))
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.BYTE], Prepare.EvmCode
                    .BYTE(16, UInt256.MaxValue.PaddedBytes(32))
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BYTE], Prepare.EvmCode
                    .BYTE(16, ((UInt256)(23)).PaddedBytes(32))
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.LOG0], Prepare.EvmCode
                    .PushData(TestBlockChain.SampleHexData1.PadLeft(64, '0'))
                    .PushData(0)
                    .Op(Instruction.MSTORE)
                    .LOGx(0, 0, 64)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.LOG1], Prepare.EvmCode
                    .PushData(TestBlockChain.SampleHexData1.PadLeft(64, '0'))
                    .PushData(0)
                    .Op(Instruction.MSTORE)
                    .PushData(TestItem.KeccakA.Bytes.ToArray())
                    .LOGx(1, 0, 64)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.LOG2], Prepare.EvmCode
                    .PushData(TestBlockChain.SampleHexData1.PadLeft(64, '0'))
                    .PushData(0)
                    .Op(Instruction.MSTORE)
                    .PushData(TestItem.KeccakA.Bytes.ToArray())
                    .PushData(TestItem.KeccakB.Bytes.ToArray())
                    .LOGx(2, 0, 64)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.LOG3], Prepare.EvmCode
                    .PushData(TestBlockChain.SampleHexData1.PadLeft(64, '0'))
                    .PushData(0)
                    .Op(Instruction.MSTORE)
                    .PushData(TestItem.KeccakA.Bytes.ToArray())
                    .PushData(TestItem.KeccakB.Bytes.ToArray())
                    .PushData(TestItem.KeccakC.Bytes.ToArray())
                    .LOGx(3, 0, 64)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.LOG4], Prepare.EvmCode
                    .PushData(TestBlockChain.SampleHexData1.PadLeft(64, '0'))
                    .PushData(0)
                    .Op(Instruction.MSTORE)
                    .PushData(TestItem.KeccakA.Bytes.ToArray())
                    .PushData(TestItem.KeccakB.Bytes.ToArray())
                    .PushData(TestItem.KeccakC.Bytes.ToArray())
                    .PushData(TestItem.KeccakD.Bytes.ToArray())
                    .LOGx(4, 0, 64)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.TSTORE, Instruction.TLOAD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(7)
                    .TSTORE()
                    .PushData(7)
                    .TLOAD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SSTORE, Instruction.SLOAD], Prepare.EvmCode
                    .PushData(UInt256.MaxValue)
                    .PushData(UInt256.MaxValue)
                    .SSTORE()
                    .PushData(UInt256.MaxValue)
                    .SLOAD()
                    .PushData(1)
                    .SSTORE()
                .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SSTORE, Instruction.SLOAD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(7)
                    .SSTORE()
                    .PushData(7)
                    .SLOAD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXTCODESIZE], Prepare.EvmCode
                    .EXTCODESIZE(Address.FromNumber(23)) // Cold Access
                    .EXTCODESIZE(Address.FromNumber(23)) // Warm Access
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXTCODESIZE], Prepare.EvmCode
                    .EXTCODESIZE(Address.FromNumber(23))
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXTCODEHASH], Prepare.EvmCode
                    .EXTCODEHASH(Address.FromNumber(23))
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXTCODECOPY], Prepare.EvmCode
                    .EXTCODECOPY(Address.FromNumber(23), 0, 5, 32)
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .EXTCODECOPY(Address.FromNumber(23), 0, 5, 32) // warm
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXTCODECOPY], Prepare.EvmCode
                    .EXTCODECOPY(Address.FromNumber(23), 0, 0, 32)
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BALANCE], Prepare.EvmCode
                    .BALANCE(Address.FromNumber(23))
                    .PushData(1)
                    .SSTORE()
                    .BALANCE(Address.FromNumber(23)) // warm access
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SELFBALANCE], Prepare.EvmCode
                    .SELFBALANCE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.INVALID], Prepare.EvmCode
                    .INVALID()
                    .PushData(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.BadInstruction, turnOnAggressiveMode);

                yield return ([Instruction.STOP], Prepare.EvmCode
                    .STOP()
                    .PushData(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.POP], Prepare.EvmCode
                    .PUSHx()
                    .POP()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.POP], Prepare.EvmCode
                    .POP()
                    .POP()
                    .POP()
                    .POP()
                    .Done, EvmExceptionType.StackUnderflow, turnOnAggressiveMode);

                yield return ([Instruction.POP], Prepare.EvmCode
                    .PushData(23)
                    .PushData(23)
                    .Op((Instruction)0x2c) // an unused opcode should be here
                    .Done, EvmExceptionType.StackUnderflow, turnOnAggressiveMode);


                for (byte opcode = (byte)Instruction.DUP1; opcode <= (byte)Instruction.DUP16; opcode++)
                {
                    int n = opcode - (byte)Instruction.DUP1 + 1;
                    var test = Prepare.EvmCode;
                    for (int i = 0; i < n; i++)
                    {
                        test.PushData(i);
                    }
                    test.Op((Instruction)opcode)
                        .PushData(1)
                        .SSTORE();

                    yield return ([(Instruction)opcode], test.Done, EvmExceptionType.None, turnOnAggressiveMode);
                }

                for (byte opcode = (byte)Instruction.PUSH0; opcode <= (byte)Instruction.PUSH32; opcode++)
                {
                    int n = opcode - (byte)Instruction.PUSH0;
                    byte[] args = n == 0 ? null : Enumerable.Range(0, n).Select(i => (byte)i).ToArray();

                    yield return ([(Instruction)opcode], Prepare.EvmCode.PUSHx(args)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);
                }

                for (byte opcode = (byte)Instruction.SWAP1; opcode <= (byte)Instruction.SWAP16; opcode++)
                {
                    int n = opcode - (byte)Instruction.SWAP1 + 2;
                    var test = Prepare.EvmCode;
                    for (int i = 0; i < n; i++)
                    {
                        test.PushData(i);
                    }
                    test.Op((Instruction)opcode)
                        .PushData(1)
                        .SSTORE();

                    yield return ([(Instruction)opcode], test.Done, EvmExceptionType.None, turnOnAggressiveMode);
                }

                yield return ([Instruction.SDIV], Prepare.EvmCode
                    .PushData(23)
                    .PushData(0)
                    .SDIV()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SDIV], Prepare.EvmCode
                    .PushData(UInt256.MaxValue)
                    .PushData((UInt256)Int256.Int256.MinusOne)
                    .SDIV()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SDIV], Prepare.EvmCode
                    .PushData(UInt256.MaxValue)
                    .PushData(7)
                    .SDIV()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SDIV], Prepare.EvmCode
                    .PushData((UInt256)new Int256.Int256(-23))
                    .PushData(7)
                    .SDIV()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SDIV], Prepare.EvmCode
                    .PushData(23)
                    .PushData(7)
                    .SDIV()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(7)
                    .SMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SMOD], Prepare.EvmCode
                    .PushData(0)
                    .PushData(7)
                    .SMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(0)
                    .SMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SMOD], Prepare.EvmCode
                    .PushData((UInt256)Int256.Int256.MinusOne)
                    .PushData(7)
                    .SMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData((UInt256)Int256.Int256.MinusOne)
                    .SMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SMOD], Prepare.EvmCode
                    .PushData(VirtualMachine.P255)
                    .PushData((UInt256)Int256.Int256.MinusOne)
                    .SMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SMOD], Prepare.EvmCode
                    .PushData((UInt256)Int256.Int256.MinusOne)
                    .PushData(VirtualMachine.P255)
                    .SMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CODECOPY], Prepare.EvmCode
                    .PushData(100)  // size
                    .PushData(3) // code start idx
                    .PushData(2) // memory start idx
                    .CODECOPY()
                    .MLOAD(2)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.CODECOPY], Prepare.EvmCode
                    .PushData(32)  // size
                    .PushData(0) // code start idx
                    .PushData(0) // memory start idx
                    .CODECOPY()
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.CODECOPY], Prepare.EvmCode
                    .PushData(0)
                    .PushData(32)
                    .PushData(7)
                    .CODECOPY()
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(10)
                    .PushData(10)
                    .PushData(1)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(3)
                    .PushData(7)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(3)
                    .PushData(0)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(3)
                    .PushData(1)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(0)
                    .PushData(7)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(1)
                    .PushData(7)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(0)
                    .PushData(3)
                    .PushData(7)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(1)
                    .PushData(3)
                    .PushData(7)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(7)
                    .PushData(23)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(7)
                    .PushData(7)
                    .PushData(23)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.KECCAK256], Prepare.EvmCode
                    .MSTORE(0, Enumerable.Range(0, 31).Select(i => (byte)i).ToArray())
                    .PushData(32) // size
                    .PushData(0) // mem start idx
                    .KECCAK256()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.KECCAK256], Prepare.EvmCode
                    .MSTORE(0, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray())
                    .PushData(16) // size
                    .PushData(16) // mem start idx
                    .KECCAK256()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.KECCAK256], Prepare.EvmCode
                    .MSTORE(0, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray())
                    .PushData(0)
                    .PushData(16)
                    .KECCAK256()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.PREVRANDAO], Prepare.EvmCode
                    .PREVRANDAO()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.RETURNDATACOPY], Prepare.EvmCode
                    .Call(Address.FromNumber((int)Instruction.RETURNDATASIZE), 10000)
                    .PushData(32) // size
                    .PushData(0) // data idx
                    .PushData(0) // mem idx
                    .RETURNDATACOPY()
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.RETURNDATACOPY], Prepare.EvmCode
                    .PushData(0)
                    .PushData(32)
                    .PushData(0)
                    .RETURNDATACOPY()
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BLOBBASEFEE], Prepare.EvmCode
                    .Op(Instruction.BLOBBASEFEE)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SIGNEXTEND], Prepare.EvmCode
                    .PushData(65525)
                    .PushData(1)
                    .SIGNEXTEND()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SIGNEXTEND], Prepare.EvmCode
                    .PushData(1023)
                    .PushData(0)
                    .SIGNEXTEND()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SIGNEXTEND], Prepare.EvmCode
                    .PushData(1024)
                    .PushData(16)
                    .SIGNEXTEND()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SIGNEXTEND], Prepare.EvmCode
                    .PushData(255)
                    .PushData(0)
                    .Op(Instruction.SIGNEXTEND)
                    .PushData(0)
                    .Op(Instruction.SSTORE)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SIGNEXTEND], Prepare.EvmCode
                    .PushData(255)
                    .PushData(32)
                    .Op(Instruction.SIGNEXTEND)
                    .PushData(0)
                    .Op(Instruction.SSTORE)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SIGNEXTEND], Prepare.EvmCode
                    .PushData(UInt256.MaxValue)
                    .PushData(31)
                    .Op(Instruction.SIGNEXTEND)
                    .PushData(0)
                    .Op(Instruction.SSTORE)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SELFDESTRUCT], Prepare.EvmCode
                    .PushData(23)
                    .PushData(3)
                    .MUL()
                    .PushData(123)
                    .SSTORE()
                    .PushData(Address.Zero)
                    .SELFDESTRUCT()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CALL], Prepare.EvmCode
                    .Call(Address.FromNumber((int)Instruction.RETURN), 10000)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.DELEGATECALL], Prepare.EvmCode
                    .DelegateCall(Address.FromNumber((int)Instruction.RETURN), 10000)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.STATICCALL], Prepare.EvmCode
                    .StaticCall(Address.FromNumber((int)Instruction.RETURN), 10000)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CALLCODE], Prepare.EvmCode
                    .CallCode(Address.FromNumber((int)Instruction.RETURN), 10000)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CREATE], Prepare.EvmCode
                    .Create(Prepare.EvmCode.STOP().Done, 0)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CREATE2], Prepare.EvmCode
                    .Create2(Prepare.EvmCode.STOP().Done, [1, 2, 3], 0)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.INVALID], Prepare.EvmCode
                    .JUMPDEST()
                    .MUL(23, 3)
                    .POP()
                    .JUMP(0)
                    .Done, EvmExceptionType.OutOfGas, turnOnAggressiveMode);

                yield return ([Instruction.INVALID], Prepare.EvmCode
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
                    .Done, EvmExceptionType.StackOverflow, turnOnAggressiveMode);

                yield return ([Instruction.INVALID], Prepare.EvmCode
                    .JUMPDEST()
                    .MUL(23)
                    .JUMP(0)
                    .Done, EvmExceptionType.StackUnderflow, turnOnAggressiveMode);

                
            }

            static IEnumerable<IReleaseSpec> GetAllFroksStarting<T>()
            {
                var baseType = typeof(T);
                var assembly = Assembly.GetAssembly(baseType);

                return assembly.GetTypes()
                                .Where(t => t != baseType && baseType.IsAssignableFrom(t) && !t.IsAbstract)
                                .Select(t => (IReleaseSpec)t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetMethod.Invoke(null, []));
            }

            IEnumerable<IReleaseSpec> forks = GetAllFroksStarting<Olympic>();

            bool[] combinations = new[]
            {
                true, false
            };

            foreach (var combination in combinations)
            {
                foreach (var sample in GetJitBytecodesSamplesGenerator(combination))
                {
                    foreach (var fork in forks)
                    {
                        yield return new($"[{String.Join(", ", sample.Item1.Select(op => op.ToString()))}]", sample.Item1, sample.Item2, sample.Item3, sample.Item4, fork);
                    }
                }
            }
        }

        [Test]
        public void All_Opcodes_Are_Covered_in_JIT_Tests()
        {
            List<Instruction> instructions = System.Enum.GetValues<Instruction>().ToList();

            var tests = GetJitBytecodesSamples()
                .SelectMany(test => test.Item2)
                .ToHashSet();

            List<Instruction> notCovered = new List<Instruction>();
            foreach (var opcode in instructions)
            {
                if (!tests.Contains(opcode))
                {
                    notCovered.Add(opcode);
                }
            }

            Assert.That(notCovered.Count, Is.EqualTo(0), $"[{String.Join(", ", notCovered)}]");
        }

        [Test]
        public void Execution_Swap_Happens_When_Compilation_Occurs()
        {
            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                IlEvmEnabledMode = ILMode.FULL_AOT_MODE,
                IlEvmAnalysisThreshold = 1,
                IlEvmAnalysisQueueMaxSize = 1,
            }, Prague.Instance);

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

            ValueHash256 codehash = Keccak.Compute(bytecode);

            for (int i = 0; i < RepeatCount; i++)
            {
                enhancedChain.Execute<ITxTracer>(bytecode, NullTxTracer.Instance);
            }

            Assert.That(AotContractsRepository.TryGetIledCode(codehash, out var iledCode), Is.True);
            Assert.That(Metrics.IlvmAotPrecompiledCalls, Is.GreaterThan(0));
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

        [Test, TestCaseSource(nameof(GetJitBytecodesSamples))]
        public void ILVM_AOT_Execution_Equivalence_Tests((string msg, Instruction[] opcode, byte[] bytecode, EvmExceptionType, bool enableAggressiveMode, IReleaseSpec spec) testcase)
        {
            Console.WriteLine(testcase.msg);

            TestBlockChain standardChain = new TestBlockChain(new VMConfig(), testcase.spec);
            Path.Combine(Directory.GetCurrentDirectory(), "GeneratedContracts.dll");

            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                IlEvmEnabledMode = ILMode.FULL_AOT_MODE,
                IlEvmAnalysisThreshold = 1,
                IlEvmAnalysisQueueMaxSize = 1,
                IsIlEvmAggressiveModeEnabled = testcase.enableAggressiveMode,
                IlEvmPersistPrecompiledContractsOnDisk = false,
            }, testcase.spec);


            byte[][] blobVersionedHashes = null;
            switch (testcase.opcode[0])
            {
                case Instruction.BLOBHASH:
                    var blobhashesCount = 10;
                    blobVersionedHashes = new byte[blobhashesCount][];
                    for (int i = 0; i < blobhashesCount; i++)
                    {
                        blobVersionedHashes[i] = new byte[32];
                        for (int n = 0; n < blobhashesCount; n++)
                        {
                            blobVersionedHashes[i][n] = (byte)((i * 3 + 10 * 7) % 256);
                        }
                    }
                    break;
                case Instruction.RETURNDATACOPY:
                    var returningCode = Prepare.EvmCode
                        .PushData(UInt256.MaxValue)
                        .PUSHx([0])
                        .MSTORE()
                        .Return(32, 0)
                        .Done;
                    var callAddress = standardChain.InsertCode(returningCode);
                    enhancedChain.InsertCode(returningCode);
                    enhancedChain.ForceRunAnalysis(callAddress, ILMode.FULL_AOT_MODE);

                    var callCode =
                        Prepare.EvmCode
                            .Call(callAddress, 10000)
                            .Done;
                    testcase.bytecode = Bytes.Concat(callCode, testcase.bytecode);
                    break;
                default:
                    break;

            }

            var address = standardChain.InsertCode(testcase.bytecode);
            enhancedChain.InsertCode(testcase.bytecode);

            var bytecode = Prepare.EvmCode
                .PushData(UInt256.MaxValue)
                .PUSHx([2])
                .MSTORE()
                .PushData(0)
                .PushData(0)
                .PushData(32) // length
                .PushData(2) // offset
                .PushData(0) // value
                .PushData(address)
                .PushData(50_000)
                .Op(Instruction.CALL)
                .STOP()
                .Done;

            standardChain.Execute<ITxTracer>(bytecode, NullTxTracer.Instance, blobVersionedHashes: blobVersionedHashes);

            enhancedChain.ForceRunAnalysis(address, ILMode.FULL_AOT_MODE);

            enhancedChain.Execute<ITxTracer>(bytecode, NullTxTracer.Instance, blobVersionedHashes: blobVersionedHashes);

            var actual = standardChain.StateRoot;
            var expected = enhancedChain.StateRoot;

            Assert.That(Metrics.IlvmAotPrecompiledCalls, Is.GreaterThan(0));
            Assert.That(actual, Is.EqualTo(expected), testcase.msg);
        }

        [Test]
        public void ILVM_AOT_Storage_Roundtrip()
        {
            TestBlockChain enhancedChain = new TestBlockChain(new VMConfig
            {
                IlEvmEnabledMode = ILMode.FULL_AOT_MODE,
                IlEvmAnalysisThreshold = 1,
                IlEvmAnalysisQueueMaxSize = 1,
                IsIlEvmAggressiveModeEnabled = true,
                IlEvmPersistPrecompiledContractsOnDisk = true,
                IlEvmPrecompiledContractsPath = Directory.GetCurrentDirectory(),
            }, Prague.Instance);

            var bytecode = Prepare.EvmCode
                .PushData(23)
                .PushData(3)
                .MUL()
                .STOP()
                .Done;

            var hashCode = Keccak.Compute(bytecode);

            var address = enhancedChain.InsertCode(bytecode);

            enhancedChain.ForceRunAnalysis(address, ILMode.FULL_AOT_MODE);

            var assemblyPath = Path.Combine(Directory.GetCurrentDirectory(), IVMConfig.DllName(hashCode.ToString()));
            var fileStream = new FileStream(assemblyPath, FileMode.OpenOrCreate);

            Assembly assembly = AssemblyLoadContext.Default.LoadFromStream(fileStream);
            MethodInfo method = assembly.GetType("ContractType").GetMethod("MoveNext");
            Assert.That(method, Is.Not.Null);
        }
    }
}
