using Nevermind.Core;

namespace Nevermind.Blockchain.Validators
{
    public class OmmersValidator
    {
        private readonly IBlockchainStore _chain;
        private readonly BlockHeaderValidator _headerValidator;

        public OmmersValidator(IBlockchainStore chain, BlockHeaderValidator headerValidator)
        {
            _chain = chain;
            _headerValidator = headerValidator;
        }
        
        public bool ValidateOmmers(Block block)
        {
            if (block.Ommers.Length > 2)
            {
                return false;
            }

            foreach (BlockHeader ommerHeader in block.Ommers)
            {
                if (_chain.FindOmmer(ommerHeader.Hash) != null)
                {
                    return false;
                }
                
                BlockHeader ommersParent = _chain.FindBlock(ommerHeader.ParentHash)?.Header;
                if (ommersParent == null)
                {
                    return false;
                }
                
                if (!IsKin(block, ommersParent, 6))
                {
                    return false;
                }

                if (!_headerValidator.IsValid(ommerHeader))
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsKin(Block block, BlockHeader ommersAncestor, int relationshipLevel)
        {
            if (relationshipLevel == 0)
            {
                return false;
            }

            if (block.Header.Hash == ommersAncestor.Hash)
            {
                return false;
            }

            if (block.Header.ParentHash == ommersAncestor.ParentHash)
            {
                return true;
            }

            BlockHeader ommersParent = _chain.FindBlock(ommersAncestor.ParentHash)?.Header ?? _chain.FindOmmer(ommersAncestor.ParentHash);
            if (ommersParent == null)
            {
                return false;
            }
            
            return IsKin(block, ommersParent, relationshipLevel - 1);
        }
    }
}