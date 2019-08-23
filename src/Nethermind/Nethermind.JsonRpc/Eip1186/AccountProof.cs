using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.JsonRpc.Eip1186.Nethermind.JsonRpc.Eip1186
{
    public class AccountProof
    {
        public byte[][] Proof { get; set; }
        
        public UInt256 Balance { get; set; }
        
        public Keccak CodeHash { get; set; }
        
        public UInt256 Nonce { get; set; }
        
        public Keccak StorageRoot { get; set; }
        
        public StorageProof[] StorageProofs { get; set; }
    }
}