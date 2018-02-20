namespace Nethermind.HashLib.Crypto.BuildIn
{
    internal class SHA1Cng : HashCryptoBuildIn
    {
        public SHA1Cng() 
            : base(new System.Security.Cryptography.SHA1CryptoServiceProvider(), 64)
        {
        }
    }
}
