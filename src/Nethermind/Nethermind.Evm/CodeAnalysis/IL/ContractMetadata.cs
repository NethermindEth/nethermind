// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis.IL;
public class ContractMetadata
{
    public CodeInfo TargetCodeInfo { get; set; }
    public OpcodeInfo[] Opcodes { get; set; }
    public int[] Jumpdests { get; set; }
    public SegmentMetadata[] Segments { get; set; }
    public byte[][] EmbeddedData { get; set; }
}

public class SegmentMetadata
{
    public OpcodeInfo[] Segment { get; set; }
    public Range Boundaries => Segment[0].ProgramCounter..(Segment[^1].ProgramCounter + Segment[^1].Metadata.AdditionalBytes);
    public Dictionary<int, SubSegmentMetadata> SubSegments { get; set; }
    public int[] StackOffsets { get; set; }
    public int[] Jumpdests { get; set; }
}

public class SubSegmentMetadata
{
    public OpcodeInfo[] SubSegment { get; set; }
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

    public Dictionary<int, long> StaticGasSubSegmentes { get; set; } = new();

    public HashSet<Instruction> Instructions => SubSegment.Select(x => x.Operation).ToHashSet();

    public bool RequiresOpcodeCheck => Instructions.Any(x => x.RequiresAvailabilityCheck());
}
