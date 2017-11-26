using Nevermind.Core;

namespace Nevermind.Blockchain.Validators
{
    public class OmmersValidator : IOmmersValidator
    {
        private readonly IBlockchainStore _chain;
        private readonly IBlockHeaderValidator _headerValidator;

        public OmmersValidator(IBlockchainStore chain, IBlockHeaderValidator headerValidator)
        {
            _chain = chain;
            _headerValidator = headerValidator;
        }
        
        public bool Validate(BlockHeader header, BlockHeader[] ommers)
        {
            if (ommers.Length > 2)
            {
                return false;
            }

            if (ommers.Length == 2 && ommers[0].Hash == ommers[1].Hash)
            {
                return false;
            }

            foreach (BlockHeader ommer in ommers)
            {
                if (_chain.FindOmmer(ommer.Hash) != null)
                {
                    return false;
                }
                
                if (!IsKin(header, ommer, 6))
                {
                    return false;
                }

                if (!_headerValidator.Validate(ommer))
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsKin(BlockHeader header, BlockHeader ommer, int relationshipLevel)
        {
            if (relationshipLevel == 0)
            {
                return false;
            }
            
            if (ommer.Number < header.Number - relationshipLevel)
            {
                return false;
            }
            
            BlockHeader parent = _chain.FindBlock(header.ParentHash)?.Header;
            if (parent == null)
            {
                return false;
            }

            if (parent.Hash == ommer.Hash)
            {
                return false;
            }

            if (parent.ParentHash == ommer.ParentHash)
            {
                return true;
            }
            
            return IsKin(parent, ommer, relationshipLevel - 1);
        }
    }
}