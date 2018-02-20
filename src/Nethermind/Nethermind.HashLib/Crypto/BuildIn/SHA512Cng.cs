namespace Nethermind.HashLib.Crypto.BuildIn
{
    internal class SHA512Cng : HashCryptoBuildIn
    {
        public SHA512Cng() 
            : base(new System.Security.Cryptography.SHA512CryptoServiceProvider(), 128)
        {
        }
    }
}
