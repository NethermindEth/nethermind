using System.Collections.Generic;

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
