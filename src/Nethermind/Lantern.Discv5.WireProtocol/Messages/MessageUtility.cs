using System.Security.Cryptography;

namespace Lantern.Discv5.WireProtocol.Messages;

public static class MessageUtility
{
    public static byte[] GenerateRequestId(int requestIdLength)
    {
        var requestId = new byte[requestIdLength];
        var random = RandomNumberGenerator.Create();
        random.GetBytes(requestId);
        return requestId;
    }
}