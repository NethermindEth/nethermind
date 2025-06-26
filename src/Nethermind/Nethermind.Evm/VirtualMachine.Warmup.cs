// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
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

        vm.SetBlockExecutionContext(new BlockExecutionContext(_header, spec));
        vm.SetTxExecutionContext(new TxExecutionContext(addressOne, codeInfoRepository, null, 0));

        ExecutionEnvironment env = new(
            executingAccount: addressOne,
            codeSource: addressOne,
            caller: addressOne,
            codeInfo: new CodeInfo(bytecode),
            value: 0,
            transferValue: 0,
            inputData: default,
            callDepth: 0);

        using (var evmState = EvmState.RentTopLevel(long.MaxValue, ExecutionType.TRANSACTION, in env, new StackAccessTracker(), state.TakeSnapshot()))
        {
            vm.EvmState = evmState;
            vm._worldState = state;
            vm._codeInfoRepository = codeInfoRepository;
            evmState.InitializeStacks();

            RunOpCodes<OnFlag>(vm, state, evmState, spec);
            RunOpCodes<OffFlag>(vm, state, evmState, spec);
        }

        TransactionProcessor processor = new(MainnetSpecProvider.Instance, state, vm, codeInfoRepository, lm);
        processor.SetBlockExecutionContext(new BlockExecutionContext(_header, spec));

        RunTransactions(processor, state, spec);
    }

    private static void RunTransactions(TransactionProcessor processor, IWorldState state, IReleaseSpec spec)
    {
        const int WarmUpIterations = 40;

        Address sender = Address.SystemUser;
        Address recipient = new("0x0000000000000000000000000000000000000100");

        state.CreateAccountIfNotExists(recipient, 100.Ether());

        List<byte> bytes = [(byte)Instruction.JUMPDEST];

        AddPrecompileCall(bytes);

        byte[] code = bytes.ToArray();

        state.InsertCode(recipient, code, spec);
        state.Commit(spec);

        Transaction tx = new()
        {
            IsServiceTransaction = true,
            GasLimit = 30_000_000,
            SenderAddress = sender,
            To = recipient
        };

        for (int i = 0; i < WarmUpIterations; i++)
        {
            processor.CallAndRestore(tx, NullTxTracer.Instance);
        }
    }

    static void AddPrecompileCall(List<byte> codeToDeploy)
    {
        byte[] x1 = Bytes.FromHexString("089142debb13c461f61523586a60732d8b69c5b38a3380a74da7b2961d867dbf");
        byte[] y1 = Bytes.FromHexString("2d5fc7bbc013c16d7945f190b232eacc25da675c0eb093fe6b9f1b4b4e107b36");
        byte[] x2 = Bytes.FromHexString("25f8c89ea3437f44f8fc8b6bfbb6312074dc6f983809a5e809ff4e1d076dd585");
        byte[] y2 = Bytes.FromHexString("0b38c7ced6e4daef9c4347f370d6d8b58f4b1d8dc61a3c59d651a0644a2a27cf");

        codeToDeploy.Add((byte)Instruction.PUSH32);     // x1
        codeToDeploy.AddRange(x1);
        codeToDeploy.Add((byte)Instruction.PUSH0);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        codeToDeploy.Add((byte)Instruction.PUSH32);     // y1
        codeToDeploy.AddRange(y1);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0x20);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        codeToDeploy.Add((byte)Instruction.PUSH32);     // x2
        codeToDeploy.AddRange(x2);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0x40);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        codeToDeploy.Add((byte)Instruction.PUSH32);     // y2
        codeToDeploy.AddRange(y2);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0x60);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        codeToDeploy.Add((byte)Instruction.JUMPDEST);

        codeToDeploy.Add((byte)Instruction.PUSH1);  // return size
        codeToDeploy.Add(0x40);
        codeToDeploy.Add((byte)Instruction.PUSH1);  // return offset
        codeToDeploy.Add(0x80);
        codeToDeploy.Add((byte)Instruction.PUSH1);  // args size
        codeToDeploy.Add(0x80);
        codeToDeploy.Add((byte)Instruction.PUSH0);  // args offset
        codeToDeploy.Add((byte)Instruction.PUSH1);  // address
        codeToDeploy.Add(0x06);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)150);
        codeToDeploy.Add((byte)Instruction.STATICCALL);
        codeToDeploy.Add((byte)Instruction.POP);
    }

    private static void RunOpCodes<TTracingInst>(VirtualMachine vm, WorldState state, EvmState evmState, IReleaseSpec spec)
        where TTracingInst : struct, IFlag
    {
        const int WarmUpIterations = 40;

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
                if (vm.ReturnData is EvmState returnState)
                {
                    returnState.Dispose();
                    vm.ReturnData = null!;
                }

                state.Reset(resetBlockChanges: true);
                stack = new(0, txTracer, evmState.DataStack);
                gas = long.MaxValue;
                pc = 0;
            }
        }
    }

    private class WarmupBlockhashProvider(ISpecProvider specProvider) : IBlockhashProvider
    {
        public Hash256 GetBlockhash(BlockHeader currentBlock, long number)
            => GetBlockhash(currentBlock, number, specProvider.GetSpec(currentBlock));

        public Hash256 GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec)
        {
            return Keccak.Compute(spec.IsBlockHashInStateAvailable
                ? (Eip2935Constants.RingBufferSize + number).ToString()
                : (number).ToString());
        }
    }
}
