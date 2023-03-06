// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm.EOF;

public readonly record struct EofHeader(byte Version,
    SectionHeader TypeSection,
    SectionHeader[] CodeSections,
    int CodeSectionsSize,
    SectionHeader DataSection);

public readonly record struct SectionHeader(int Start, ushort Size)
{
    public int EndOffset => Start + Size;
}

public static class EofExtensions
{
    public static void GetSectionMetadata(this ICodeInfo codeinfo, int sectionIndex, out int sectionInput, out int sectionOutput, out int maxStackHeight)
    {

        sectionInput = codeinfo.TypeSection.Span[sectionIndex * EvmObjectFormat.Eof1.MINIMUM_TYPESECTION_SIZE];
        sectionOutput = codeinfo.TypeSection.Span[sectionIndex * EvmObjectFormat.Eof1.MINIMUM_TYPESECTION_SIZE + EvmObjectFormat.Eof1.OUTPUTS_OFFSET];
        maxStackHeight = codeinfo.TypeSection.Span[sectionIndex * EvmObjectFormat.Eof1.MINIMUM_TYPESECTION_SIZE + EvmObjectFormat.Eof1.MAX_STACK_HEIGHT_OFFSET];
    }
}
