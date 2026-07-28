// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.CodeAnalysis.IlEvm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nethermind.Evm.Benchmark
{
    /// <summary>
    /// End-to-end IL-EVM effectiveness check on a tight arithmetic loop (the shape of the
    /// ecc-heavy corpus): the same bytecode through the real VM with IL-EVM off vs on.
    /// This is the local ground truth for "is the region compiler actually engaged and how
    /// much does it buy per loop iteration".
    /// </summary>
    [MemoryDiagnoser]
    public class IlEvmLoopBenchmark
    {
        private const int LoopIterations = 5000;

        private readonly IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.IstanbulBlockNumber);
        private readonly BlockHeader _header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, long.MaxValue, 1UL, Bytes.Empty);

        private IVirtualMachine _virtualMachine;
        private IWorldState _stateProvider;
        private IDisposable _worldStateScope;
        private byte[] _code;
        private CodeInfo _interpreterCodeInfo;
        private CodeInfo _compiledCodeInfo;

        [GlobalSetup]
        public void GlobalSetup()
        {
            _code = BuildLoopCode();

            _stateProvider = TestWorldStateFactory.CreateForTest();
            _worldStateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether);
            _stateProvider.Commit(_spec);
            EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
            _virtualMachine = new EthereumVirtualMachine(new TestBlockhashProvider(), MainnetSpecProvider.Instance, LimboLogs.Instance);
            _virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(_header, _spec));
            _virtualMachine.SetTxExecutionContext(new TxExecutionContext(Address.Zero, codeInfoRepository, null, 0));

            _interpreterCodeInfo = new CodeInfo(_code);

            // Pre-compile the IL-EVM artifact synchronously so the measurement is steady-state.
            _compiledCodeInfo = new CodeInfo(_code);
            bool enabledBackup = Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled;
            int thresholdBackup = Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.CompileThreshold;
            bool syncBackup = Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.SynchronousCompilation;
            Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled = true;
            Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.CompileThreshold = 1;
            Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.SynchronousCompilation = true;
            Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.NoticeExecution(_compiledCodeInfo, _spec);
            Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled = enabledBackup;
            Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.CompileThreshold = thresholdBackup;
            Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.SynchronousCompilation = syncBackup;

            IlCompiledCode artifact = Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.GetForExecution(_compiledCodeInfo, _spec);
            Console.WriteLine(artifact is null
                ? "!!! IL-EVM artifact MISSING — the IlEvm benchmark measures the interpreter"
                : $"IL-EVM artifact ready: {artifact.SegmentCount} entry segment(s)");
        }

        [GlobalCleanup]
        public void GlobalCleanup() => _worldStateScope?.Dispose();

        [Benchmark(Baseline = true)]
        public void Interpreter()
        {
            Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled = false;
            Execute(_interpreterCodeInfo);
        }

        [Benchmark]
        public void IlEvm()
        {
            Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled = true;
            Execute(_compiledCodeInfo);
        }

        private void Execute(CodeInfo codeInfo)
        {
            using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
                executingAccount: Address.Zero,
                codeSource: Address.Zero,
                caller: Address.Zero,
                codeInfo: codeInfo,
                callDepth: 0,
                value: 0,
                inputData: default);
            using VmState<EthereumGasPolicy> evmState = VmState<EthereumGasPolicy>.RentTopLevel(
                EthereumGasPolicy.FromLong(30_000_000), ExecutionType.TRANSACTION, environment, new StackAccessTracker(), _stateProvider.TakeSnapshot());
            _virtualMachine.ExecuteTransaction<OffFlag>(evmState, _stateProvider, NullTxTracer.Instance);
            _stateProvider.Reset();
        }

        /// <summary>
        /// acc = 0; for (i = LoopIterations; i != 0; i--) acc = (acc + 7) * 3 ^ i;
        /// then MSTORE + RETURN. The loop body and head are compilable blocks with a constant
        /// JUMPI exit and constant JUMP back-edge — the exact v4 region shape.
        /// </summary>
        private static byte[] BuildLoopCode() =>
        [
            (byte)Instruction.PUSH1, 0,                  // pc 0: acc
            (byte)Instruction.PUSH3, 0, (LoopIterations >> 8) & 0xFF, LoopIterations & 0xFF, // pc 2: i
            (byte)Instruction.JUMPDEST,                  // pc 6: loop  [acc, i]
            (byte)Instruction.DUP1,                      // pc 7        [acc, i, i]
            (byte)Instruction.ISZERO,                    // pc 8        [acc, i, i==0]
            (byte)Instruction.PUSH1, 29,                 // pc 9        [.., end]
            (byte)Instruction.JUMPI,                     // pc 11       [acc, i]
            (byte)Instruction.SWAP1,                     // pc 12       [i, acc]
            (byte)Instruction.PUSH1, 7,                  // pc 13
            (byte)Instruction.ADD,                       // pc 15       [i, acc+7]
            (byte)Instruction.PUSH1, 3,                  // pc 16
            (byte)Instruction.MUL,                       // pc 18       [i, 3(acc+7)]
            (byte)Instruction.DUP2,                      // pc 19       [i, m, i]
            (byte)Instruction.XOR,                       // pc 20       [i, m^i]
            (byte)Instruction.SWAP1,                     // pc 21       [acc', i]
            (byte)Instruction.PUSH1, 1,                  // pc 22
            (byte)Instruction.SWAP1,                     // pc 24       [acc', 1, i]
            (byte)Instruction.SUB,                       // pc 25       [acc', i-1]
            (byte)Instruction.PUSH1, 6,                  // pc 26
            (byte)Instruction.JUMP,                      // pc 28       → loop
            (byte)Instruction.JUMPDEST,                  // pc 29: end  [acc, 0]
            (byte)Instruction.POP,                       // pc 30       [acc]
            (byte)Instruction.PUSH1, 0,                  // pc 31
            (byte)Instruction.MSTORE,                    // pc 33
            (byte)Instruction.PUSH1, 32,                 // pc 34
            (byte)Instruction.PUSH1, 0,                  // pc 36
            (byte)Instruction.RETURN,                    // pc 38
        ];
    }
}
