// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy> where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    /// <summary>Warms EVM instructions using the mainnet BPO2 spec.</summary>
    public static void WarmUpEvmInstructions(IWorldState state, ICodeInfoRepository codeInfoRepository) =>
        WarmUpEvmInstructions(state, codeInfoRepository, MainnetSpecProvider.Instance, MainnetSpecProvider.BPO2Activation);

    /// <summary>Warms EVM instructions for the chain spec at the supplied activation.</summary>
    public static void WarmUpEvmInstructions(IWorldState state, ICodeInfoRepository codeInfoRepository, ISpecProvider specProvider, ForkActivation activation)
    {
        IBlockhashProvider hashProvider = new WarmupBlockhashProvider(specProvider);
        VirtualMachine<TGasPolicy> vm = new(hashProvider, specProvider, LimboLogs.Instance);
        ILogManager lm = new OneLoggerLogManager(NullLogger.Instance);

        byte[] bytecode = new byte[64];
        bytecode.AsSpan().Fill((byte)Instruction.JUMPDEST);
        byte[] address = new byte[20];
        address[^1] = 0x1;
        Address addressOne = new(address);

        BlockHeader header = new(Keccak.Zero, Keccak.Zero, addressOne, UInt256.One, activation.BlockNumber, Int64.MaxValue, activation.Timestamp ?? 0, Bytes.Empty, 0, 0);
        IReleaseSpec spec = specProvider.GetSpec(header);
        state.CreateAccount(addressOne, 1000.Ether);
        state.Commit(spec);

        vm.SetBlockExecutionContext(new BlockExecutionContext(header, spec));
        vm.SetTxExecutionContext(new TxExecutionContext(addressOne, codeInfoRepository, null, 0));

        using ExecutionEnvironment env = ExecutionEnvironment.Rent(
            codeInfo: new CodeInfo(bytecode),
            executingAccount: addressOne,
            caller: addressOne,
            codeSource: addressOne,
            callDepth: 0,
            value: 0,
            inputData: default);

        using (VmState<TGasPolicy> vmState = VmState<TGasPolicy>.RentTopLevel(TGasPolicy.FromULong(ulong.MaxValue), ExecutionType.TRANSACTION, env, new StackAccessTracker(), state.TakeSnapshot()))
        {
            vm.VmState = vmState;
            vm._worldState = state;
            vm._codeInfoRepository = codeInfoRepository;

            WarmUpOpcodeHandlers<OffFlag, OffFlag>(vm, state, vmState);
            WarmUpOpcodeHandlers<OffFlag, OnFlag>(vm, state, vmState);
            WarmUpOpcodeHandlers<OnFlag, OffFlag>(vm, state, vmState);
            WarmUpOpcodeHandlers<OnFlag, OnFlag>(vm, state, vmState);
        }

        TransactionProcessor<TGasPolicy> processor = new(BlobBaseFeeCalculator.Instance, specProvider, state, vm, codeInfoRepository, lm);
        processor.SetBlockExecutionContext(new BlockExecutionContext(header, spec));

        RunTransactions(processor, state, spec);
    }

    private static void RunTransactions(TransactionProcessor<TGasPolicy> processor, IWorldState state, IReleaseSpec spec)
    {
        const int WarmUpIterations = 40;

        Address sender = Address.SystemUser;
        // EIP-7951 reserves 0x100 for P256VERIFY; the warmup recipient must execute bytecode.
        Address recipient = Address.FromNumber(0x10000);

        state.CreateAccountIfNotExists(recipient, 100.Ether);

        List<byte> bytes = [(byte)Instruction.JUMPDEST];

        AddPrecompileCall(bytes, spec);

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

    static void AddPrecompileCall(List<byte> codeToDeploy, IReleaseSpec spec)
    {
        byte[] x1 = Bytes.FromHexString("089142debb13c461f61523586a60732d8b69c5b38a3380a74da7b2961d867dbf");
        byte[] y1 = Bytes.FromHexString("2d5fc7bbc013c16d7945f190b232eacc25da675c0eb093fe6b9f1b4b4e107b36");
        byte[] x2 = Bytes.FromHexString("25f8c89ea3437f44f8fc8b6bfbb6312074dc6f983809a5e809ff4e1d076dd585");
        byte[] y2 = Bytes.FromHexString("0b38c7ced6e4daef9c4347f370d6d8b58f4b1d8dc61a3c59d651a0644a2a27cf");

        codeToDeploy.Add((byte)Instruction.PUSH32);     // x1
        codeToDeploy.AddRange(x1);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0);
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
        codeToDeploy.Add((byte)Instruction.PUSH1); // args offset
        codeToDeploy.Add(0);
        if (!spec.IsEip214Enabled)
        {
            codeToDeploy.Add((byte)Instruction.PUSH1);
            codeToDeploy.Add(0);
        }
        codeToDeploy.Add((byte)Instruction.PUSH1);  // address
        codeToDeploy.Add(0x06);
        // BN254 addition costs 500 gas before EIP-1108 and 150 afterwards.
        codeToDeploy.Add((byte)Instruction.PUSH2);
        codeToDeploy.Add(0x01);
        codeToDeploy.Add(0xf4);
        codeToDeploy.Add((byte)(spec.IsEip214Enabled ? Instruction.STATICCALL : Instruction.CALL));
        codeToDeploy.Add((byte)Instruction.POP);
    }

    private static void WarmUpOpcodeHandlers<TTracingInst, TCancelable>(
        VirtualMachine<TGasPolicy> vm,
        IWorldState state,
        VmState<TGasPolicy> vmState)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        const int WarmUpIterations = 40;

        ITxTracer txTracer = new FeesTracer();
        vm._txTracer = txTracer;
        // This drives RunByteCode directly, so it resolves the table itself rather than through a transaction.
        vm.PrepareOpcodes<TTracingInst, TCancelable>();
        byte[] code = new byte[EvmStack.WordSize + 2];
        vmState.InitializeStacks(txTracer, code, out EvmStack stack);

        for (int repeat = 0; repeat < WarmUpIterations; repeat++)
        {
            for (int i = 0; i <= byte.MaxValue; i++)
            {
                code.AsSpan().Clear();
                Instruction instruction = (Instruction)i;
                code[0] = (byte)instruction;
                if (instruction is Instruction.JUMP or Instruction.JUMPI)
                    code[1] = (byte)Instruction.JUMPDEST;
                else if (instruction is Instruction.DUPN or Instruction.SWAPN or Instruction.EXCHANGE)
                    code[1] = 0x80;

                for (int stackItem = 0; stackItem < 20; stackItem++)
                    stack.PushOne<TTracingInst>();

                vmState.ProgramCounter = 0;
                vmState.Gas = TGasPolicy.FromULong(ulong.MaxValue);
                CallResult callResult = vm.RunByteCode<TTracingInst, TCancelable>(ref stack, ref vmState.Gas);
                callResult.StateToExecute?.Dispose();

                state.Reset(resetBlockChanges: true);
                stack.Head = 0;
            }
        }
    }

    private class WarmupBlockhashProvider(ISpecProvider specProvider) : IBlockhashProvider
    {
        public Hash256 GetBlockhash(BlockHeader currentBlock, ulong number)
            => GetBlockhash(currentBlock, number, specProvider.GetSpec(currentBlock));

        public Hash256 GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec) => Keccak.Compute(spec!.IsBlockHashInStateAvailable
                ? (Eip2935Constants.RingBufferSize + number).ToString()
                : number.ToString());

        public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => Task.CompletedTask;
    }
}
