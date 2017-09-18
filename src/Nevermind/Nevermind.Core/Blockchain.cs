using System;
using System.Collections.Generic;
using Nevermind.Core.Encoding;

namespace Nevermind.Core
{
    public class Blockchain
    {
        private static readonly Dictionary<Keccak, Block> Blocks = new Dictionary<Keccak, Block>();

        public static void AddBlock(Block block)
        {
            Blocks.Add(block.Hash, block);
        }

        public static Block GetParent(Block block)
        {
            return GetBlock(block.Header.ParentHash);
        }

        public static Block GetParent(BlockHeader header)
        {
            return GetBlock(header.ParentHash);
        }

        public static Block GetBlock(BlockHeader blockHeader)
        {
            throw new NotImplementedException();
        }

        public static Block GetBlock(Keccak blockHash)
        {
            return Blocks.ContainsKey(blockHash) ? Blocks[blockHash] : null;
        }
    }
}