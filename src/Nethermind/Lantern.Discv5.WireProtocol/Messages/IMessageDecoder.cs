namespace Lantern.Discv5.WireProtocol.Messages;

public interface IMessageDecoder
{
    Message DecodeMessage(byte[] message);
}