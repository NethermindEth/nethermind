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
internal class ILEvmState
{
    // static arguments
    public BlockHeader Header;

    public byte[] bytes;
    public UInt256[] Stack;

    // in case of exceptions
    public EvmExceptionType EvmException;

    // in case of jumps crossing section boundaries
    public ushort ProgramCounter;
    public int GasAvailable;

    // in case STOP is executed
    public bool StopExecution;
}
