// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Specs.ChainSpecStyle
{
    /// <summary>
    /// https://github.com/ethereum/wiki/wiki/Ethereum-Chain-Spec-Format
    /// https://wiki.parity.io/Chain-specification
    /// </summary>
    [DebuggerDisplay("{Name}, ChainId = {ChainId}")]
    public class ChainSpec
    {
        public string Name { get; set; }

        /// <summary>
        /// Not used in Nethermind
        /// </summary>
        public string DataDir { get; set; }

        public ulong NetworkId { get; set; }

        public ulong ChainId { get; set; }

        public NetworkNode[] Bootnodes { get; set; }

        public bool GenesisStateUnavailable { get; set; }
        public Block Genesis { get; set; }

        public string SealEngineType { get; set; }

        public AuRaParameters AuRa { get; set; }

        public CliqueParameters Clique { get; set; }

        public EthashParameters Ethash { get; set; }

        public OptimismParameters Optimism { get; set; }

        public ChainParameters Parameters { get; set; }

        public Dictionary<Address, ChainSpecAllocation> Allocations { get; set; }

        public long? FixedDifficulty { get; set; }

        public long? DaoForkBlockNumber { get; set; }

        public long? HomesteadBlockNumber { get; set; }

        public long? TangerineWhistleBlockNumber { get; set; }

        public long? SpuriousDragonBlockNumber { get; set; }

        public long? ByzantiumBlockNumber { get; set; }

        public long? ConstantinopleBlockNumber { get; set; }

        public long? ConstantinopleFixBlockNumber { get; set; }

        public long? IstanbulBlockNumber { get; set; }

        public long? MuirGlacierNumber { get; set; }

        public long? BerlinBlockNumber { get; set; }

        public long? LondonBlockNumber { get; set; }

        public long? ArrowGlacierBlockNumber { get; set; }

        public long? GrayGlacierBlockNumber { get; set; }

        public long? MergeForkIdBlockNumber { get; set; }

        public long? TerminalPoWBlockNumber { get; set; }

        public UInt256? TerminalTotalDifficulty { get; set; }

        public ulong? ShanghaiTimestamp { get; set; }

        public ulong? CancunTimestamp { get; set; }
    }
}
