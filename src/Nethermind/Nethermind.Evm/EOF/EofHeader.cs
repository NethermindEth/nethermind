using System.Linq;

namespace Nethermind.Evm.EOF;

public record struct EofHeader(byte Version,
    SectionHeader? TypeSection,
    SectionHeaderCollection CodeSections,
    SectionHeader? DataSection)
{
    public int HeaderSize => 2 + 1 + (TypeSection is null ? 0 : 1 + 2) + (DataSection is null ? 0 : 1 + 2) + (3 * CodeSections.Count) + 1;
}

public record struct SectionHeader(int Start, ushort Size)
{
    public int EndOffset => Start + Size;
}
