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
    public required int PrefixSize;
    public required SectionHeader TypeSection;
    public required CompoundSectionHeader CodeSections;
    public required SectionHeader DataSection;
    public required CompoundSectionHeader? ContainerSection;
}

public record struct SectionHeader(int Start, ushort Size)
{
    public int EndOffset => Start + Size;
}

public record struct CompoundSectionHeader(int Start, int[] SubSectionsSizes)
{
    public readonly int EndOffset = Start + SubSectionsSizes.Sum();
    public int Size => EndOffset - Start;
    public int Count => SubSectionsSizes.Length;

    private int[] subSectionsSizesAcc;
    private int[] SubSectionsSizesAcc
    {
        get
        {
            if (subSectionsSizesAcc is null)
            {
                subSectionsSizesAcc = new int[SubSectionsSizes.Length];
                subSectionsSizesAcc[0] = 0;
                for (var i = 1; i < SubSectionsSizes.Length; i++)
                {
                    subSectionsSizesAcc[i] = subSectionsSizesAcc[i - 1] + SubSectionsSizes[i - 1];
                }
            }

            return subSectionsSizesAcc;
        }
    }

    public SectionHeader this[int i] => new SectionHeader(Start: SubSectionsSizesAcc[i], Size: (ushort)SubSectionsSizes[i]);
}
