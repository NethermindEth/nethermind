using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Eip1186
{
    public class StorageProof
    {
        public byte[][] Proof { get; set; }
        public Keccak Key { get; set; }
        public byte[] Value { get; set; }
    }
}