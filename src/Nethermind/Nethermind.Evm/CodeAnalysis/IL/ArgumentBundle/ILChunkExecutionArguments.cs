// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.State;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis.IL.ArgumentBundle
{
    public ref struct ILChunkExecutionArguments
    {
        public ref byte MachineCode;
        public IReleaseSpec Spec;
        public ISpecProvider SpecProvider;

        public IBlockhashProvider BlockhashProvider;
        public ICodeInfoRepository CodeInfoRepository;
        public EvmState EvmState;

        public ref readonly ExecutionEnvironment Environment;
        public ref readonly TxExecutionContext TxExecutionContext;
        public ref readonly BlockExecutionContext BlockExecutionContext;

        public IWorldState WorldState;
        public ReadOnlyMemory<byte> ReturnDataBuffer;

        public ref long GasAvailable;
        public ref int ProgramCounter;
        public ref int StackHead;
        public ref Word StackHeadRef;

        public ILChunkExecutionArguments(ref byte machineCode, IReleaseSpec spec, ISpecProvider specProvider, IBlockhashProvider blockhashProvider, ICodeInfoRepository codeInfoRepository, EvmState evmState, IWorldState worldState, ReadOnlyMemory<byte> returnDataBuffer, ref long gasAvailable, ref int programCounter, ref int stackHead, ref Word stackHeadRef)
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

            Environment = ref evmState.Env;
            TxExecutionContext = ref Environment.TxExecutionContext;
            BlockExecutionContext = ref TxExecutionContext.BlockExecutionContext;
        }
    }
}
