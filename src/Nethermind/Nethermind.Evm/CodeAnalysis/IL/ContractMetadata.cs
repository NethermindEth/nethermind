// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis.IL;

public struct ContractCompilerMetadata
{
    public CodeInfo TargetCodeInfo { get; set; }
    public List<SegmentMetadata> Segments { get; set; }
    public Dictionary<int, short> StackOffsets { get; set; }
    public Dictionary<int, long> StaticGasSubSegmentes { get; set; }
}

public class SegmentMetadata
{
    public Range Boundaries { get; set; }
    public Dictionary<int, SubSegmentMetadata> SubSegments { get; set; }
    public int[] Jumpdests { get; set; }
}

public class SubSegmentMetadata
{
    public int Start { get; set; }
    public int End { get; set; }

    public bool IsReachable { get; set; }
    public bool IsFailing { get; set; }

    public int RequiredStack { get; set; }
    public int MaxStack { get; set; }
    public int LeftOutStack { get; set; }

    public void SetInitialStackData(int required, int max, int leftOut)
    {
        RequiredStack = required;
        MaxStack = max;
        LeftOutStack = leftOut;
    }

    public HashSet<Instruction> Instructions { get; set; }
    public bool RequiresStaticEnvCheck { get; set; }
    public bool RequiresOpcodeCheck { get; set; }
}
