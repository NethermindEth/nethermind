#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Newtonsoft.Json.Linq;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using static Nethermind.Specs.ChainSpecStyle.Json.ChainSpecJson;
using Nethermind.Specs.ChainSpecStyle.Json.GethSpecStyle;

namespace Nethermind.Specs.GethSpecStyle
{
    internal class GethGenesisJson
    {
        public GethConfigJson? Config { get; set; }
        public ulong? Nonce { get; set; }
        public UInt256? Timestamp { get; set; }
        public Keccak? ParentHash { get; set; }
        public byte[]? ExtraData { get; set; }
        public long? GasLimit { get; set; }
        public UInt256? Difficulty { get; set; }
        public Keccak? Mixhash { get; set; }
        public Address? Coinbase { get; set; }
        public Dictionary<string, GethAllocationJson>? Alloc { get; set; }
        public ChainSpecJson? ToParityChainsSpec() => GethSpecConverter.ToParityChainsSpec(this);
            
    }
}
