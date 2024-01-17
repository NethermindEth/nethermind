// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Drawing;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm.EOF;

public struct EofHeader
{
    public required byte Version;
    public required SectionHeader TypeSection;
    public required CompoundSectionHeader CodeSections;
    public required SectionHeader DataSection;
    public required CompoundSectionHeader? ContainerSection;

    public EofHeader()
    {
    }
}

public readonly record struct SectionHeader(int Start, ushort Size)
{
    public int EndOffset => Start + Size;
}

public readonly record struct CompoundSectionHeader(int Start, int[] SubSectionsSizes)
{
    public int EndOffset => Start + SubSectionsSizes.Sum();
    public int Size => EndOffset - Start;
    public int Count => SubSectionsSizes.Length;

    public SectionHeader this[int i] => new SectionHeader(Start: Start + SubSectionsSizes[..i].Sum(), Size: (ushort)SubSectionsSizes[i]);
}
