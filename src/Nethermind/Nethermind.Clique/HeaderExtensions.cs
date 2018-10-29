using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;

namespace Nethermind.Clique
{
    public static class HeaderExtensions
    {
//        public static Address GetAuthor(this BlockHeader header, ISpecProvider specProvider)
//        {
//            if (header.Author != null)
//            {
//                return header.Author;
//            }
//            
//            if (specProvider == MainNetSpecProvider.Instance)
//            {
//                header.Author = header.Beneficiary;
//            }
//            
//            if (specProvider == RopstenSpecProvider.Instance)
//            {
//                header.Author = header.Beneficiary;
//            }
//            
//            if (specProvider == RinkebySpecProvider.Instance)
//            {
//                header.Author = GetBlockSealer(header);
//            }
//            
//            return header.Author;
//        }

        public static Address GetBlockSealer(this BlockHeader header, LruCache<Keccak, Address> signatures = null)
        {
            int extraSeal = 65;
            Address address = signatures?.Get(header.Hash);
            if (address != null)
            {
                return address;
            }
            // Retrieve the signature from the header extra-data
            if (header.ExtraData.Length < extraSeal)
            {
                return null;
            }
            byte[] signatureBytes = header.ExtraData.Slice(header.ExtraData.Length - extraSeal, extraSeal);
            Signature signature = new Signature(signatureBytes);
            signature.V += 27;
            // Recover the Ethereum address
            // TODO Get SpecProvider or EthereumSigner from Runner
            EthereumSigner signer = new EthereumSigner(RinkebySpecProvider.Instance, NullLogManager.Instance);
            Keccak message = header.HashCliqueHeader();
            address = signer.RecoverAddress(signature, message);
            signatures?.Set(header.Hash, address);
            return address;
        }

        public static Keccak HashCliqueHeader(this BlockHeader blockHeader)
        {
            int extraSeal = 65;
            int shortExtraLength = blockHeader.ExtraData.Length - extraSeal;
            byte[] fullExtraData = blockHeader.ExtraData;
            byte[] shortExtraData = blockHeader.ExtraData.Slice(0, shortExtraLength);
            blockHeader.ExtraData = shortExtraData;
            Rlp rlp = Rlp.Encode(blockHeader);
            Keccak sigHash = Keccak.Compute(rlp);
            blockHeader.ExtraData = fullExtraData;
            return sigHash;
        }
    }
}