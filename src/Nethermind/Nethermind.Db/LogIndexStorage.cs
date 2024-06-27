using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Db
{

    public class LogIndexStorage
    {
        public IEnumerable<long> GetBlocksForAddress(Address address)
        {
            throw new NotImplementedException();
        }


        public IEnumerable<long> GetBlocksForTopic(Hash256 topic)
        {
            throw new NotImplementedException();
        }

    }

}
