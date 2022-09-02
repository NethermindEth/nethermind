#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using static Nethermind.Specs.ChainSpecStyle.Json.ChainSpecJson;

namespace Nethermind.Specs.GethSpecStyle
{
    internal class GethConfig
    {
        public ulong? ChainId { get; set; }
        public long? HomesteadBlock { get; set; }

        public long? DAOForkBlock { get; set; }
        public bool? DAOForkSupport { get; set; }

        public long? Eip150Block { get; set; }
        public string? Eip150Hash { get; set; }

        public long? Eip155Block { get; set; }
        public long? Eip158Block { get; set; }
        public long? Eip160Block { get; set; }
        public long? ByzantiumBlock { get; set; }
        public long? ConstantinopleBlock { get; set; }
        public long? TangerWistleBlock { get; set; }
        public long? PetersburgBlock { get; set; }
        public long? IstanbulBlock { get; set; }
        public long? MuirGlacierBlock { get; set; }
        public long? BerlinBlock { get; set; }
        public long? LondonBlock { get; set; }
        public long? ArrowGlacierBlock { get; set; }
        public long? GrayGlacierBlock { get; set; }
        public long? MergeNetSplitBlock { get; set; }
        public long? ShanghaiBlock{ get; set; }
        public long? CancunBlock { get; set; }

        public UInt256? TerminalTotalDifficulty { get; set; }
        public bool TerminalTotalDifficultyPassed { get; set; }

        public EthashEngineParamsJson? Ethash { get; set; }
        public CliqueEngineParamsJson? Clique { get; set; }
        public AuraEngineParamsJson? Aura { get; set; }
    }
}
