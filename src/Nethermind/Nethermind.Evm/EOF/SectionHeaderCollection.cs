using System.Linq;

namespace Nethermind.Evm.EOF;

public record struct SectionHeaderCollection(SectionHeader[] ChildSections)
{
    public int Start = ChildSections[0].Start;
    public SectionHeader this[int i] => ChildSections[i];
    public int Size => ChildSections?.Sum(section => section.Size) ?? 0;
    public int Count => ChildSections is null ? 0 : ChildSections.Length;
    public int EndOffset => Start + Size;
}
