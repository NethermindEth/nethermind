namespace Nevermind.Core.Crypto
{
    public interface ICryptoRandom
    {
        byte[] GenerateRandomBytes(int lenght);
        int NextInt(int max);
    }
}