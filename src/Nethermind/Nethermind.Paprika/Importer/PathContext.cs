using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Paprika.Data;

namespace Nethermind.Paprika.Importer;

[StructLayout(LayoutKind.Explicit, Size = SizeOf)]
public struct PathContext : INodeContext<PathContext>
{
    public const int SizeOf = PathLength + sizeof(int);
    private const int PathLength = 64;

    [FieldOffset(0)]
    public byte First;

    [FieldOffset(PathLength)] public int Length;

    public Span<byte> Span => MemoryMarshal.CreateSpan(ref First, PathLength);

    public PathContext Add(ReadOnlySpan<byte> nibblePath)
    {
        var added = NibblePath.FromRawNibbles(nibblePath, stackalloc byte[(nibblePath.Length + 1) / 2]);
        var current = NibblePath.FromKey(Span).SliceTo(Length);

        Span<byte> workingSet = stackalloc byte[current.MaxByteLength + added.MaxByteLength + 2];
        var appended = current.Append(added, workingSet);

        var result = default(PathContext);
        appended.RawSpan.CopyTo(result.Span);
        result.Length = appended.Length;

        return result;
    }

    public PathContext Add(byte nibble)
    {
        var current = NibblePath.FromKey(Span).SliceTo(Length);

        Span<byte> workingSet = stackalloc byte[current.MaxByteLength + 2];
        var appended = current.AppendNibble(nibble, workingSet);

        var result = default(PathContext);
        appended.RawSpan.CopyTo(result.Span);
        result.Length = appended.Length;

        return result;
    }

    public PathContext AddStorage(in ValueHash256 storage)
    {
        return this;
    }

    public NibblePath AsNibblePath => NibblePath.FromKey(Span).SliceTo(Length);
}
