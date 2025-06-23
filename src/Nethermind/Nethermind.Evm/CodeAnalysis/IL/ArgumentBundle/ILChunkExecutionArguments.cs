// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis.IL.ArgumentBundle
{
    public ref struct ILChunkExecutionArguments
    {
        // dont move MachineCode from the top, it is used in the ILChunkExecutionState struct
        public ref byte MachineCode;
        public ref long GasAvailable;
        public ref int ProgramCounter;
        public ref int StackHead;
        public ref Word StackHeadRef;
        public ref EvmPooledMemory Memory;
        public ref readonly ExecutionEnvironment Environment;
        public ref readonly TxExecutionContext TxExecutionContext;
        public ref readonly BlockExecutionContext BlockExecutionContext;

        public ReadOnlyMemory<byte> ReturnDataBuffer;

        public EvmState EvmState;
        public IReleaseSpec Spec;
        public ISpecProvider SpecProvider;
        public IBlockhashProvider BlockhashProvider;
        public ICodeInfoRepository CodeInfoRepository;
        public IWorldState WorldState;

        public ITxTracer TxTracer;
        public ILogger Logger;

        public ILChunkExecutionArguments(ref byte machineCode, ref long gasAvailable, ref int programCounter, ref int stackHead, ref Word stackHeadRef, IReleaseSpec spec, ISpecProvider specProvider, IBlockhashProvider blockhashProvider, ICodeInfoRepository codeInfoRepository, EvmState evmState, IWorldState worldState, ReadOnlyMemory<byte> returnDataBuffer, ITxTracer txTracer, ILogger logger)
        {
            MachineCode = ref machineCode;
            Spec = spec;
            SpecProvider = specProvider;
            BlockhashProvider = blockhashProvider;
            CodeInfoRepository = codeInfoRepository;
            EvmState = evmState;
            WorldState = worldState;
            ReturnDataBuffer = returnDataBuffer;
            GasAvailable = ref gasAvailable;
            ProgramCounter = ref programCounter;
            StackHead = ref stackHead;
            StackHeadRef = ref stackHeadRef;

            TxTracer = txTracer;
            Logger = logger;

            Environment = ref evmState.Env;
            TxExecutionContext = ref Environment.TxExecutionContext;
            BlockExecutionContext = ref TxExecutionContext.BlockExecutionContext;

            Memory = ref evmState.Memory;
        }
    }
}
