using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Db
{

    public class LogIndexStorage
    {
        private Dictionary<Address, List<long>> _addressToBlocks = new Dictionary<Address, List<long>>();
        public void StoreLogIndex(Address address, long blockNumber)
        {

            if (!_addressToBlocks.TryGetValue(address, out List<long> blocks))
            {
                blocks = new List<long>();
                _addressToBlocks[address] = blocks;
            }
            blocks.Add(blockNumber);

        }

        public IEnumerable<long> GetBlocksForAddress(Address address)
        {
            if (_addressToBlocks.TryGetValue(address, out List<long> blocks))
            {
                return blocks;
            }
            return Array.Empty<long>();
        }


        public IEnumerable<long> GetBlocksForTopic(Hash256 topic)
        {
            return [1, 2, 3, 4, 5, 6];
        }

    }

}
