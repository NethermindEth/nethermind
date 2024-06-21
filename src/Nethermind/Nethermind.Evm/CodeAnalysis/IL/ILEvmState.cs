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
    public byte[] MachineCode;
    // static arguments
    public ref ExecutionEnvironment Env;
    public ref TxExecutionContext TxCtx;
    public ref BlockExecutionContext BlkCtx;
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

    public int StackHead;
    public Span<byte> Stack;

    public ref EvmPooledMemory Memory;

    public ref ReadOnlyMemory<byte> InputBuffer;
    public ref ReadOnlyMemory<byte> ReturnBuffer;

    public ILEvmState(byte[] machineCode, ref ExecutionEnvironment env, ref TxExecutionContext txCtx, ref BlockExecutionContext blkCtx, EvmExceptionType evmException, ushort programCounter, long gasAvailable, int stackHead, Span<byte> stack, ref EvmPooledMemory memory, ref ReadOnlyMemory<byte> inputBuffer, ref ReadOnlyMemory<byte> returnBuffer)
    {
        MachineCode = machineCode;
        Env = ref env;
        TxCtx = ref txCtx;
        BlkCtx = ref blkCtx;
        EvmException = evmException;
        ProgramCounter = programCounter;
        GasAvailable = gasAvailable;
        ShouldStop = false;
        ShouldRevert = false;
        ShouldReturn = false;
        ShouldJump = false;
        StackHead = stackHead;
        Stack = stack;
        Memory = ref memory;
        ReturnBuffer = ref returnBuffer;
        InputBuffer = ref inputBuffer;
    }
}
