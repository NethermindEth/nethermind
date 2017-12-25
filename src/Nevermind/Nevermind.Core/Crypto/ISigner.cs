namespace Nevermind.Core.Crypto
{
    public interface ISigner
    {
        Signature Sign(PrivateKey privateKey, Keccak message);
        void Sign(PrivateKey privateKey, Transaction transaction);
        Address Recover(Signature signature, Keccak message);
        bool Verify(Address sender, Transaction transaction);
        Address Recover(Transaction transaction);
    }
}