namespace Nethermind.Evm.EOF;

public record struct EofHeader(byte Version,
    SectionHeader CodeSection,
    SectionHeader? DataSection)
{
    public int HeaderSize => 2 + 1 + (DataSection is null ? 0 : 1 + 2) + 3 + 1;
    // MagicLength + Version + 1 * (SectionSeparator + SectionSize) + HeaderTerminator = 2 + 1 + 1 * (1 + 2) + 1 = 7
}

public record struct SectionHeader(int Start, ushort Size)
{
    public int EndOffset => Start + Size;
}
