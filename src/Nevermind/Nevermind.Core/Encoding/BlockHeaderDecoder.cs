using System.Numerics;
using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;

namespace Nevermind.Core.Encoding
{
    public class BlockHeaderDecoder : IRlpDecoder<BlockHeader>
    {
        internal BlockHeader Decode(object[] data)
        {
            Keccak parentHash = new Keccak((byte[])data[0]);
            Keccak ommersHash = new Keccak((byte[])data[1]);
            Address beneficiary = new Address((byte[])data[2]);
            Keccak stateRoot = new Keccak((byte[])data[3]);
            Keccak transactionsRoot = new Keccak((byte[])data[4]);
            Keccak receiptsRoot = new Keccak((byte[])data[5]);
            Bloom bloom = new Bloom(((byte[])data[6]).ToBigEndianBitArray2048());
            BigInteger difficulty = ((byte[])data[7]).ToUnsignedBigInteger();
            BigInteger number = ((byte[])data[8]).ToUnsignedBigInteger();
            BigInteger gasLimit = ((byte[])data[9]).ToUnsignedBigInteger();
            BigInteger gasUsed = ((byte[])data[10]).ToUnsignedBigInteger();
            BigInteger timestamp = ((byte[])data[11]).ToUnsignedBigInteger();
            byte[] extraData = (byte[])data[12];
            Keccak mixHash = new Keccak((byte[])data[13]);
            BigInteger nonce = ((byte[])data[14]).ToUnsignedBigInteger();

            BlockHeader blockHeader = new BlockHeader(
                parentHash,
                ommersHash,
                beneficiary,
                difficulty,
                number,
                (long)gasLimit,
                timestamp,
                extraData);

            blockHeader.StateRoot = stateRoot;
            blockHeader.TransactionsRoot = transactionsRoot;
            blockHeader.ReceiptsRoot = receiptsRoot;
            blockHeader.Bloom = bloom;
            blockHeader.GasUsed = (long)gasUsed;
            blockHeader.MixHash = mixHash;
            blockHeader.Nonce = (ulong)nonce;
            blockHeader.RecomputeHash(); // TODO: why
            return blockHeader;
        }
        
        public BlockHeader Decode(Rlp rlp)
        {
            object[] header = (object[])Rlp.Decode(rlp);
            return Decode(header);
        }
    }
}