using System;
using System.Collections.Generic;
using System.IO.Pipes;
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

        public IEnumerable<long> GetUnionFromAddresses(HashSet<AddressAsKey> addresses)
        {
            List<int> blockNumbers1 = new List<int>(new int[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            List<int> blockNumbers2 = new List<int>(new int[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 });

            var firstEnumerator = blockNumbers1.GetEnumerator();
            var secondEnumerator = blockNumbers2.GetEnumerator();

            bool firstHasNext = firstEnumerator.MoveNext();
            bool secondHasNext = secondEnumerator.MoveNext();

            while (firstHasNext || secondHasNext)
            {

                if (firstHasNext && secondHasNext)
                {
                    var subtract = firstEnumerator.Current - secondEnumerator.Current;

                    switch (subtract)
                    {
                        case < 0: yield return firstEnumerator.Current; firstHasNext = firstEnumerator.MoveNext(); break;
                        case > 0: yield return secondEnumerator.Current; secondHasNext = secondEnumerator.MoveNext(); break;
                        default: yield return firstEnumerator.Current; firstHasNext = firstEnumerator.MoveNext(); secondHasNext = secondEnumerator.MoveNext(); break;
                    }
                }
                else if (firstHasNext)
                {
                    yield return firstEnumerator.Current;
                    firstHasNext = firstEnumerator.MoveNext();
                }
                else if (secondHasNext)
                {
                    yield return secondEnumerator.Current;
                    secondHasNext = secondEnumerator.MoveNext();
                }
            }
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
