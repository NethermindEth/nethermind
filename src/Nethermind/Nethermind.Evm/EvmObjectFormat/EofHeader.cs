// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;

namespace Nethermind.Evm.EOF;

public struct EofHeader()
{
    public required byte Version;
    public required int PrefixSize;
    public required SectionHeader TypeSection;
    public required CompoundSectionHeader CodeSections;
    public required CompoundSectionHeader? ContainerSections;
    public required SectionHeader DataSection;
}

public struct SectionHeader(int start, ushort size)
{
    public readonly int Start => start;
    public readonly int Size => size;
    public readonly int EndOffset => Start + Size;

    public static implicit operator Range(SectionHeader section) => new(section.Start, section.EndOffset);
}

public struct CompoundSectionHeader(int start, int[] subSectionsSizes)
{
    public readonly int Start => start;

    public readonly int[] SubSectionsSizes = subSectionsSizes;

    public readonly int EndOffset => Start + SubSectionsSizes.Sum();
    public readonly int Size => EndOffset - Start;
    public readonly int Count => SubSectionsSizes.Length;

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

    public SectionHeader this[int i] => new SectionHeader(SubSectionsSizesAcc[i], (ushort)SubSectionsSizes[i]);

    public static implicit operator Range(CompoundSectionHeader section) => new(section.Start, section.EndOffset);
}

