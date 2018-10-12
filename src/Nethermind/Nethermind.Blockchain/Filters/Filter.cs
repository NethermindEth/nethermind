using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Filters
{
    public class Filter : FilterBase
    {
        public Keccak FromBlock { get; set; }
        public Keccak ToBlock { get; set; }
        public FilterAddress Address { get; set; }
        public IEnumerable<FilterTopic> Topics { get; set; }
    }
}