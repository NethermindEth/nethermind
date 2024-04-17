// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public byte[] bytes;
    public UInt256[] Stack;
    public EvmException EvmException;
    public int ProgramCounter;
    public int GasAvailable;

    public static FieldInfo GetFieldInfo(string name)
    {
        return typeof(ILEvmState).GetField(name);
    }
}
