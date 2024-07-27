// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using static Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript.Log;

namespace Nethermind.Evm.CodeAnalysis.IL;
internal ref struct ILEvmState
{
    public ReadOnlyMemory<byte> MachineCode;
    public EvmState EvmState;
    // static arguments
    // * vmState.Env :
    public ref readonly ExecutionEnvironment Env;
    // * vmState.Env.TxCtx :
    public ref readonly TxExecutionContext TxCtx;
    // * vmState.Env.TxCtx.BlkCtx :
    public ref readonly BlockExecutionContext BlkCtx;
    // in case of exceptions
    public EvmExceptionType EvmException;
    // in case of jumps crossing section boundaries
    public ushort ProgramCounter;
    public long GasAvailable;
    // in case STOP is executed
    public bool ShouldStop;
    public bool ShouldRevert;
    public bool ShouldReturn;
    public bool ShouldJump;

    // * vmState.DataStackHead :
    public int StackHead;
    // * vmState.DataStack :
    public Span<byte> Stack;

    // * vmState.Memory :
    public ref EvmPooledMemory Memory;

    public ref readonly ReadOnlyMemory<byte> InputBuffer;
    public ref ReadOnlyMemory<byte> ReturnBuffer;

    public ILEvmState(EvmState evmState, EvmExceptionType evmException, ushort programCounter, long gasAvailable, ref ReadOnlyMemory<byte> returnBuffer)
    {
        // locals for ease of access
        EvmState = evmState; 
        MachineCode = evmState.Env.CodeInfo.MachineCode;
        Env = ref evmState.Env;
        TxCtx = ref evmState.Env.TxExecutionContext;
        BlkCtx = ref evmState.Env.TxExecutionContext.BlockExecutionContext;
        StackHead = evmState.DataStackHead;
        Stack = evmState.DataStack;
        Memory = ref evmState.Memory;

        EvmException = evmException;
        ProgramCounter = programCounter;
        GasAvailable = gasAvailable;

        InputBuffer = ref evmState.Env.InputData;
        ReturnBuffer = ref returnBuffer;

        ShouldStop = false;
        ShouldRevert = false;
        ShouldReturn = false;
        ShouldJump = false;
    }
}
