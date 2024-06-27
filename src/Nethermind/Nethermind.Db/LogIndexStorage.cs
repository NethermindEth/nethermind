using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Db
{

    public class LogIndexStorage
    {
        public static IEnumerable<long> GetBlocksForAddress(Address address)
        {
            throw new NotImplementedException();
        }


        public static IEnumerable<long> GetBlocksForTopic(Hash256 topic)
        {
            throw new NotImplementedException();
        }

    }

}
