using System;

namespace Nevermind.Core
{
    public class Blockchain
    {
        public static Block GetParent(Block block)
        {
            if (block.Header.Number == 0)
            {
                return null;
            }

            return GetBlock(block.Header.Number - 1);
        }

        public static Block GetBlock(long blockNumber)
        {
            throw new NotImplementedException();
        }
    }
}