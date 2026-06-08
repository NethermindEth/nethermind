// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Int256;
using System.Collections.Generic;
using System.Diagnostics;

namespace Nethermind.Specs.ChainSpecStyle
{
    /// <summary>
    /// https://github.com/ethereum/wiki/wiki/Ethereum-Chain-Spec-Format
    /// https://openethereum.github.io/Chain-specification
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

        public NetworkNode[] Bootnodes { get; set; } = [];

        public bool GenesisStateUnavailable { get; set; }
        public Block Genesis { get; set; }

        public string SealEngineType { get; set; }

        public ChainParameters Parameters { get; set; }

        public IChainSpecParametersProvider EngineChainSpecParametersProvider { get; set; }

        public Dictionary<Address, ChainSpecAllocation> Allocations { get; set; }

        public long? FixedDifficulty { get; set; }

        public ulong? DaoForkBlockNumber { get; set; }

        public ulong? HomesteadBlockNumber { get; set; }

        public ulong? TangerineWhistleBlockNumber { get; set; }

        public ulong? SpuriousDragonBlockNumber { get; set; }

        public ulong? ByzantiumBlockNumber { get; set; }

        public ulong? ConstantinopleBlockNumber { get; set; }

        public ulong? ConstantinopleFixBlockNumber { get; set; }

        public ulong? IstanbulBlockNumber { get; set; }

        public ulong? MuirGlacierNumber { get; set; }

        public ulong? BerlinBlockNumber { get; set; }

        public ulong? LondonBlockNumber { get; set; }

        public ulong? ArrowGlacierBlockNumber { get; set; }

        public ulong? GrayGlacierBlockNumber { get; set; }

        public ulong? MergeForkIdBlockNumber { get; set; }

        public ulong? TerminalPoWBlockNumber { get; set; }

        public UInt256? TerminalTotalDifficulty { get; set; }

        public ulong? ShanghaiTimestamp { get; set; }

        public ulong? CancunTimestamp { get; set; }

        public ulong? PragueTimestamp { get; set; }

        public ulong? OsakaTimestamp { get; set; }
        public ulong? AmsterdamTimestamp { get; set; }
    }
}
