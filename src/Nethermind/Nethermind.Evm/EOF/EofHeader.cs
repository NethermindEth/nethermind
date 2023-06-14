// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm.EOF;

public struct EofHeader
{
    public required byte Version;
    public required HeaderOffsets offsets;
    public required SectionHeader TypeSection;
    public required SectionHeader[] CodeSections;
    public required int CodeSectionsSize;
    public required SectionHeader DataSection;
    public required SectionHeader[]? ContainerSection;
    public required int ExtraContainersSize = 0;

    public EofHeader()
    {
    }
}

public readonly record struct SectionHeader(int Start, ushort Size)
{
    public int EndOffset => Start + Size;
}

public struct HeaderOffsets
{
    public int TypeSectionHeaderOffset;
    public int CodeSectionHeaderOffset;
    public int DataSectionHeaderOffset;
    public int ContainerSectionHeaderOffset;
    public int EndOfHeaderOffset;

    public void Deconstruct(
        out (int start, int size) typeSectionHeaderOffsets,
        out (int start, int size) codeSectionHeaderOffsets,
        out (int start, int size) dataSectionHeaderOffsets,
        out (int start, int size) containerSectionHeaderOffsets)
    {
        typeSectionHeaderOffsets = (TypeSectionHeaderOffset, CodeSectionHeaderOffset - TypeSectionHeaderOffset);
        codeSectionHeaderOffsets = (CodeSectionHeaderOffset, DataSectionHeaderOffset - CodeSectionHeaderOffset);
        dataSectionHeaderOffsets = (DataSectionHeaderOffset, ContainerSectionHeaderOffset - DataSectionHeaderOffset);
        containerSectionHeaderOffsets = (ContainerSectionHeaderOffset, EndOfHeaderOffset - ContainerSectionHeaderOffset);
    }
}
