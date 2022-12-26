namespace Nethermind.Evm.EOF;

public record struct EofHeader(byte Version,
    SectionHeader TypeSection,
    SectionHeader[] CodeSections,
    SectionHeader DataSection);

public record struct SectionHeader(int Start, ushort Size)
{
    public int EndOffset => Start + Size;
}
