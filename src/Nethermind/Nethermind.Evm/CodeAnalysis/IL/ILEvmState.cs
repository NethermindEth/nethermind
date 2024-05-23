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

namespace Nethermind.Evm.CodeAnalysis.IL;
internal ref struct ILEvmState
{
    // static arguments
    public BlockHeader Header;
    public ExecutionEnvironment Env;
    public TxExecutionContext TxCtx;
    public BlockExecutionContext BlkCtx;

    public Span<byte> Stack;

    // in case of exceptions
    public EvmExceptionType EvmException;

    // in case of jumps crossing section boundaries
    public ushort ProgramCounter;
    public long GasAvailable;

    // in case STOP is executed
    public bool StopExecution;
}
