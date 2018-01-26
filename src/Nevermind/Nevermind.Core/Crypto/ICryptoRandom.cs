namespace Nevermind.Core.Crypto
{
    public interface ICryptoRandom
    {
        byte[] GenerateRandomBytes(int lenght);
    }
}