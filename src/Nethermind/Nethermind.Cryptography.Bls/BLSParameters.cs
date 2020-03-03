namespace Nethermind.Cryptography
{
    public struct BLSParameters
    {
        public byte[] InputKeyMaterial;
        public byte[] PrivateKey;
        public byte[] PublicKey;
        public BlsScheme Scheme;
        public BlsVariant Variant;
    }
}
