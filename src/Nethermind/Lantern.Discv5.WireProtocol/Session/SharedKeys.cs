namespace Lantern.Discv5.WireProtocol.Session;

public class SharedKeys(byte[] keyData)
{
    public byte[] InitiatorKey { get; } = keyData[..16];

    public byte[] RecipientKey { get; } = keyData[16..];
}