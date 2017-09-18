namespace Nevermind.Core.Validators
{
    public class OmmersValidator
    {
        public static bool ValidateOmmers(Block block)
        {
            if (block.Ommers.Length > 2)
            {
                return false;
            }

            foreach (BlockHeader ommerHeader in block.Ommers)
            {
                if (!IsKin(block, Blockchain.GetBlock(ommerHeader), 6))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsKin(Block block, Block ommer, int relationshipLevel)
        {
            if (relationshipLevel == 0)
            {
                return false;
            }

            if (block.Hash == ommer.Hash)
            {
                return false;
            }

            if (block.Parent.Hash == ommer.Header.ParentHash)
            {
                return true;
            }

            return IsKin(block, Blockchain.GetBlock(ommer.Header.ParentHash), relationshipLevel - 1);
        }
    }
}