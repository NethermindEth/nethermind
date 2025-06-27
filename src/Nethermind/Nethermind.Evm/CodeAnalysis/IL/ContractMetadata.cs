// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Org.BouncyCastle.Asn1.Mozilla;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis.IL;

public struct ContractCompilerMetadata
{
    public Dictionary<int, short> StackOffsets { get; set; }
    public Dictionary<int, long> StaticGasSubSegmentes { get; set; }
    public Dictionary<int, SubSegmentMetadata> SubSegments { get; set; }
}

public class SubSegmentMetadata
{
    public bool IsEntryPoint { get; set; }

    public int Start { get; set; }
    public int End { get; set; }

    public bool IsReachable { get; set; }
    public bool IsFailing { get; set; }

    public int RequiredStack { get; set; }
    public int MaxStack { get; set; }
    public int LeftOutStack { get; set; }

    public HashSet<Instruction> Instructions { get; set; }
    public bool RequiresStaticEnvCheck { get; set; }
    public bool RequiresOpcodeCheck { get; set; }

    public bool IsEphemeralCall => Instructions.Any(opcode => opcode.IsCreate() || opcode.IsCall());
    public bool IsEphemeralJump => Instructions.Any(opcode => opcode.IsJump());
}
