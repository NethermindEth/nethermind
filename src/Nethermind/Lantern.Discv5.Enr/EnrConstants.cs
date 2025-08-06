namespace Lantern.Discv5.Enr;

public static class EnrConstants
{
    public const int EnrPrefixLength = 4;

    public static readonly byte[] ProtoBufferPrefix = { 0x08, 0x02, 0x12, 0x21 };
}