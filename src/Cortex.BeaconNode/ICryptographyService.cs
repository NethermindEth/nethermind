using Cortex.Containers;

namespace Cortex.BeaconNode
{
    public interface ICryptographyService
    {
        bool BlsVerify(BlsPublicKey publicKey, Hash32 signingRoot, BlsSignature signature, Domain domain);
        Hash32 Hash(Hash32 a, Hash32 b);
    }
}