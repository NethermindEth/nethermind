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
internal struct ILEvmState
{
    // static arguments
    public BlockHeader Header;

    public byte[] StackBytes
    {
        set
        {
            if(value.Length % 32 != 0)
            {
                throw new ArgumentException("Invalid byte array length");
            }

            Stack = new UInt256[value.Length / 32];
            for(int i = 0; i < value.Length; i += 32)
            {
                Stack[i / 32] = new UInt256(value[i..(i + 32)]);
            }
        }

        get
        {
            byte[] result = new byte[Stack.Length * 32];
            for(int i = 0; i < Stack.Length; i++)
            {
                Stack[i].PaddedBytes(32).CopyTo(result, i * 32);
            }
            return result;
        }
    }

    public UInt256[] Stack;

    // in case of exceptions
    public EvmExceptionType EvmException;

    // in case of jumps crossing section boundaries
    public ushort ProgramCounter;
    public int GasAvailable;

    // in case STOP is executed
    public bool StopExecution;
}
