// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Evm;

using unsafe OpCode = delegate*<VirtualMachine, ref EvmStack, ref long, ref int, EvmExceptionType>;

public unsafe partial class VirtualMachine
{
    public static void WarmUpEvmInstructions()
    {
        IReleaseSpec spec = Fork.GetLatest();
        IBlockhashProvider hashProvider = new WarmupBlockhashProvider(MainnetSpecProvider.Instance);
        VirtualMachine vm = new(hashProvider, MainnetSpecProvider.Instance, LimboLogs.Instance);
        ILogManager lm = new OneLoggerLogManager(NullLogger.Instance);

        IKeyValueStoreWithBatching db = new MemDb();
        TrieStore trieStore = new(new NodeStorage(db), No.Pruning, Persist.EveryBlock, new PruningConfig(), lm);

        byte[] bytecode = new byte[64];
        bytecode.AsSpan().Fill((byte)Instruction.JUMPDEST);
        byte[] address = new byte[20];
        address[^1] = 0x1;
        Address addressOne = new(address);

        WorldState state = new(trieStore, db, lm);
        state.CreateAccount(addressOne, 1000.Ether());
        state.Commit(spec);
        CodeInfoRepository codeInfoRepository = new();
        BlockHeader _header = new(Keccak.Zero, Keccak.Zero, addressOne, UInt256.One, MainnetSpecProvider.PragueActivation.BlockNumber, Int64.MaxValue, 1UL, Bytes.Empty, 0, 0);
        ExecutionEnvironment env = new
        (
            executingAccount: addressOne,
            codeSource: addressOne,
            caller: addressOne,
            codeInfo: new CodeInfo(bytecode),
            value: 0,
            transferValue: 0,
            txExecutionContext: new TxExecutionContext(new BlockExecutionContext(_header, spec), addressOne, 0, null, codeInfoRepository),
            inputData: default
        );

        using var evmState = EvmState.RentTopLevel(long.MaxValue, ExecutionType.TRANSACTION, state.TakeSnapshot(), env, new StackAccessTracker());

        vm.EvmState = evmState;
        vm._worldState = state;
        vm._spec = spec;
        vm._codeInfoRepository = codeInfoRepository;
        evmState.InitializeStacks();

        RunOpCodes<OnFlag>(vm, state, evmState, spec);
        RunOpCodes<OffFlag>(vm, state, evmState, spec);
    }

    private static void RunOpCodes<TTracingInst>(VirtualMachine vm, WorldState state, EvmState evmState, IReleaseSpec spec)
        where TTracingInst : struct, IFlag
    {
        const int WarmUpIterations = 30;

        OpCode[] opcodes = EvmInstructions.GenerateOpCodes<TTracingInst>(spec);
        ITxTracer txTracer = new FeesTracer();
        vm._txTracer = txTracer;
        EvmStack stack = new(0, txTracer, evmState.DataStack);
        long gas = long.MaxValue;
        int pc = 0;

        for (int repeat = 0; repeat < WarmUpIterations; repeat++)
        {
            for (int i = 0; i < opcodes.Length; i++)
            {
                // LOG4 needs 6 values on stack
                stack.PushOne<TTracingInst>();
                stack.PushOne<TTracingInst>();
                stack.PushOne<TTracingInst>();
                stack.PushOne<TTracingInst>();
                stack.PushOne<TTracingInst>();
                stack.PushOne<TTracingInst>();

                opcodes[i](vm, ref stack, ref gas, ref pc);

                state.Reset(resetBlockChanges: true);
                stack = new(0, txTracer, evmState.DataStack);
                gas = long.MaxValue;
                pc = 0;
            }
        }
    }

    private class WarmupBlockhashProvider(ISpecProvider specProvider) : IBlockhashProvider
    {
        public Hash256 GetBlockhash(BlockHeader currentBlock, in long number)
        {
            IReleaseSpec spec = specProvider.GetSpec(currentBlock);
            return Keccak.Compute(spec.IsBlockHashInStateAvailable
                ? (Eip2935Constants.RingBufferSize + number).ToString()
                : (number).ToString());
        }
    }
}
