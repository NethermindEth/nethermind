using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Ethereum.Test.Base.T8NUtils;

public class T8NBlockHashProvider : IBlockhashProvider
{
    private readonly Dictionary<long, Hash256?> _blockHashes = new();
    public Hash256 GetBlockhash(BlockHeader currentBlock, in long number)
    {
        return _blockHashes.GetValueOrDefault(number, null)
               ?? throw new T8NException($"BlockHash for block {number} not provided", ExitCodes.ErrorMissingBlockhash);
    }

    public void Insert(Hash256 blockHash, long number)
    {
        _blockHashes[number] = blockHash;
    }
}
