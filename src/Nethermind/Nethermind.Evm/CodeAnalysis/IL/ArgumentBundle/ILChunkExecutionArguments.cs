// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using System;

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
        public ref readonly ExecutionEnvironment Environment;

        public ReadOnlyMemory<byte> ReturnDataBuffer;

        public EvmState EvmState;
        public readonly VirtualMachine Vm;

        public ILogger Logger;

        public ILChunkExecutionArguments(
            ref byte machineCode, ref long gasAvailable, ref int programCounter, ref int stackHead, ref Word stackHeadRef,
            VirtualMachine vm, EvmState evmState, ReadOnlyMemory<byte> returnDataBuffer, ILogger logger)
        {
            MachineCode = ref machineCode;
            Vm = vm;
            EvmState = evmState;
            ReturnDataBuffer = returnDataBuffer;
            GasAvailable = ref gasAvailable;
            ProgramCounter = ref programCounter;
            StackHead = ref stackHead;
            StackHeadRef = ref stackHeadRef;
            Logger = logger;

            Environment = ref evmState.Env;
        }
    }
}
