using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Crypto;

namespace Nevermind.Blockchain
{
    public class BlockStore : IBlockStore
    {
        private readonly Dictionary<Keccak, Block> _branches = new Dictionary<Keccak, Block>();
        private readonly Dictionary<Keccak, Block> _mainChain = new Dictionary<Keccak, Block>();

        public void AddBlock(Block block, bool isMainChain)
        {
            if (isMainChain)
            {
                _mainChain.Add(block.Header.Hash, block);
                _processed.Add(block.Header.Hash);
            }
            else
            {
                _branches.Add(block.Header.Hash, block);
            }
        }

        public Block FindBlock(Keccak blockHash, bool mainChainOnly)
        {
            _mainChain.TryGetValue(blockHash, out Block block);
            if (block == null && !mainChainOnly)
            {
                _branches.TryGetValue(blockHash, out block);
            }

            return block;
        }

        public Block FindBlock(BigInteger blockNumber)
        {
            return _mainChain.Values.FirstOrDefault(x => x.Header?.Number == blockNumber);
        }

        public bool IsMainChain(Keccak blockHash)
        {
            return _mainChain.ContainsKey(blockHash);
        }

        public void MoveToBranch(Keccak blockHash)
        {
            _branches.Add(blockHash, _mainChain[blockHash]);
            _mainChain.Remove(blockHash);
        }

        private readonly HashSet<Keccak> _processed = new HashSet<Keccak>();
        
        public bool WasProcessed(Keccak blockHash)
        {
            return _processed.Contains(blockHash);
        }

        public void MoveToMain(Keccak blockHash)
        {
            _mainChain.Add(blockHash, _branches[blockHash]);
            _processed.Add(blockHash);
            _branches.Remove(blockHash);
        }
    }
}