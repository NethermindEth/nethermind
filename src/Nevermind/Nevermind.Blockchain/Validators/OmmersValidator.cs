using Nevermind.Core;

namespace Nevermind.Blockchain.Validators
{
    public class OmmersValidator
    {
        private readonly IBlockchainStore _blockchain;

        public OmmersValidator(IBlockchainStore blockchain)
        {
            _blockchain = blockchain;
        }
        
        public bool ValidateOmmers(Block block)
        {
            if (block.Ommers.Length > 2)
            {
                return false;
            }

            foreach (BlockHeader ommerHeader in block.Ommers)
            {
                if (!IsKin(block, _blockchain.FindBlock(ommerHeader.Hash), 6))
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsKin(Block block, Block ommer, int relationshipLevel)
        {
            if (relationshipLevel == 0)
            {
                return false;
            }

            if (block.Header.Hash == ommer.Header.Hash)
            {
                return false;
            }

            if (block.Header.ParentHash == ommer.Header.ParentHash)
            {
                return true;
            }

            return IsKin(block, _blockchain.FindBlock(ommer.Header.ParentHash), relationshipLevel - 1);
        }
    }
}