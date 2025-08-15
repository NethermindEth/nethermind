using System.Net;

namespace Lantern.Discv5.WireProtocol.Messages;

public interface IMessageResponder
{
    Task<byte[][]?> HandleMessageAsync(byte[] message, IPEndPoint endPoint);
}