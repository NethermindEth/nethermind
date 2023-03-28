// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.EOF;

public readonly record struct EofHeader(byte Version,
    SectionHeader TypeSection,
    SectionHeader[] CodeSections,
    int CodeSectionsSize,
    SectionHeader DataSection,
    SectionHeader[]? ContainerSection,
    int ExtraContainersSize = 0
);

public readonly record struct SectionHeader(int Start, ushort Size)
{
    public int EndOffset => Start + Size;
}
