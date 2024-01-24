// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Drawing;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm.EOF;

public struct EofHeader()
{
    public required byte Version;
    public required SectionHeader TypeSection;
    public required CompoundSectionHeader CodeSections;
    public required SectionHeader DataSection;
    public required CompoundSectionHeader? ContainerSection;
}

public readonly record struct SectionHeader(int Start, ushort Size)
{
    public int EndOffset => Start + Size;
}

public readonly record struct CompoundSectionHeader(int Start, int[] SubSectionsSizes)
{
    public readonly int EndOffset = Start + SubSectionsSizes.Sum();
    public int Size => EndOffset - Start;
    public int Count => SubSectionsSizes.Length;

    /*
    private readonly int[] subSectionsSizesAcc;
    private readonly int[] SubSectionsSizesAcc
    {
        init
        {
            if(subSectionsSizesAcc is null) {
                subSectionsSizesAcc = new int[SubSectionsSizes.Length];
            }

            for (var i = 0; i < SubSectionsSizes.Length; i++)
            {
                if(i == 0)
                {
                    subSectionsSizesAcc[i] = 0;
                } else
                {
                    subSectionsSizesAcc[i] = subSectionsSizesAcc[i - 1] + SubSectionsSizes[i];
                }
            }
        }

        get => subSectionsSizesAcc;
    }

    public SectionHeader this[int i] => new SectionHeader(Start: SubSectionsSizesAcc[i], Size: (ushort)SubSectionsSizes[i]);
    */
    // returns a subsection with localized indexing [0, size] ==> 0 is local to the section not the entire bytecode
    public SectionHeader this[int i] => new SectionHeader(Start: SubSectionsSizes[..i].Sum(), Size: (ushort)SubSectionsSizes[i]);
}
