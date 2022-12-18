namespace Nethermind.Evm.EOF;

enum SectionDividor : byte
{
    Terminator = 0,
    CodeSection = 1,
    DataSection = 2,
    TypeSection = 3,
}
