using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Merkleization;

namespace Nethermind.BeaconNode
{
    public class DepositStore : IDepositStore
    {
        private readonly ICryptographyService _crypto;

        // keep in the storage
        // split storage of deposit data and deposit where deposit is deposit data + proof
        public IList<Deposit> Deposits { get; } = new List<Deposit>();

        public DepositStore(ICryptographyService crypto, ChainConstants chainConstants)
        {
            _crypto = crypto;
        }
        
        public Deposit Place(DepositData depositData)
        {
            Ref<DepositData> depositDataRef = depositData.OrRoot;
            Root leaf = _crypto.HashTreeRoot(depositDataRef);
            Bytes32 leafBytes = Bytes32.Wrap(leaf.Bytes);
            DepositData.Insert(leafBytes);
            
            var proof = DepositData.GetProof(DepositData.Count - 1);
            byte[] indexBytes = new byte[32];
            BinaryPrimitives.WriteUInt32LittleEndian(indexBytes, DepositData.Count);
            Bytes32 indexHash = new Bytes32(indexBytes);
            proof.Add(indexHash);
            
            Deposit deposit = new Deposit(proof, depositDataRef);
            Deposits.Add(deposit);
            return deposit;
        }

        public IMerkleList DepositData { get; set; } = new ShaMerkleTree();
    }
}