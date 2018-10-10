using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Blockchain.Filters
{
    public class FilterAddress
    {
        public byte[] Data { get; set; }
        public IEnumerable<Address> Addresses { get; set; }
    }
}