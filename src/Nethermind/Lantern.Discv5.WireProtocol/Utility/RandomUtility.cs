using System.Security.Cryptography;

namespace Lantern.Discv5.WireProtocol.Utility;

public static class RandomUtility
{
    public static byte[] GenerateRandomData(int size)
    {
        using var random = RandomNumberGenerator.Create();

        var bytes = new byte[size];
        random.GetBytes(bytes);

        return bytes;
    }
}