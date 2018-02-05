namespace Nevermind.Core.Crypto
{
    public interface ICryptoRandom
    {
        byte[] GenerateRandomBytes(int length);
        int NextInt(int max);
    }
}