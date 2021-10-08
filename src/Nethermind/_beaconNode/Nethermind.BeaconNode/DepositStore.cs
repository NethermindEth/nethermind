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
        private readonly ChainConstants _chainConstants;

        // keep in the storage
        // split storage of deposit data and deposit where deposit is deposit data + proof
        public IList<Deposit> Deposits { get; } = new List<Deposit>();

        public DepositStore(ICryptographyService crypto, ChainConstants chainConstants)
        {
            _crypto = crypto;
            _chainConstants = chainConstants;
        }
        
        public Deposit Place(DepositData depositData)
        {
            Ref<DepositData> depositDataRef = depositData.OrRoot;
            Root leaf = _crypto.HashTreeRoot(depositDataRef);
            Bytes32 leafBytes = Bytes32.Wrap(leaf.Bytes);
            DepositData.Insert(leafBytes);
            
            var proof = DepositData.GetProof(DepositData.Count - 1);

            Deposit deposit = new Deposit(proof, depositDataRef);
            Deposits.Add(deposit);
            return deposit;
        }

        public bool Verify(Deposit deposit)
        {
            // TODO: need to be able to delete?
            // generally need to understand how the verification would work here and the current code at least
            // encapsulates deposits creation and verification
            
            Root depositDataRoot = _crypto.HashTreeRoot(deposit.Data);
            Bytes32 rootBytes = new Bytes32(depositDataRoot.AsSpan());
            VerificationData.Insert(Bytes32.Wrap(deposit.Data.Root.Bytes));
            bool isValid = VerificationData.VerifyProof(rootBytes, deposit.Proof, VerificationData.Count - 1);
            return isValid;
        }

        public IMerkleList DepositData { get; set; } = new ShaMerkleTree();
        
        public IMerkleList VerificationData { get; set; } = new ShaMerkleTree();
    }
}