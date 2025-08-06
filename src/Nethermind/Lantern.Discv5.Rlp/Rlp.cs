namespace Lantern.Discv5.Rlp;

public readonly struct Rlp(ReadOnlyMemory<byte> src, int prefixLength)
{
    public ReadOnlyMemory<byte> Source { get; } = src;
    public int PrefixLength { get; } = prefixLength;

    public static implicit operator ReadOnlyMemory<byte>(Rlp self) => self.Source[self.PrefixLength..];
    public static implicit operator byte[](Rlp self) => self.Source[self.PrefixLength..].ToArray();
}
