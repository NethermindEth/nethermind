// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal class ChainSpecParamsJson
    {
        public ulong? ChainId { get; set; }
        public ulong? NetworkId { get; set; }

        [JsonProperty(PropertyName = "registrar")]
        public Address EnsRegistrar { get; set; }

        public long? GasLimitBoundDivisor { get; set; }

        public UInt256? AccountStartNonce { get; set; }

        public long? MaximumExtraDataSize { get; set; }

        public long? MinGasLimit { get; set; }

        public long? ForkBlock { get; set; }

        public Keccak ForkCanonHash { get; set; }

        public long? Eip7Transition { get; set; }

        public long? Eip150Transition { get; set; }

        public long? Eip152Transition { get; set; }

        public long? Eip160Transition { get; set; }

        public long? Eip161abcTransition { get; set; }

        public long? Eip161dTransition { get; set; }

        public long? Eip155Transition { get; set; }

        public long? MaxCodeSize { get; set; }

        public long? MaxCodeSizeTransition { get; set; }

        public long? Eip140Transition { get; set; }

        public long? Eip211Transition { get; set; }

        public long? Eip214Transition { get; set; }

        public long? Eip658Transition { get; set; }

        public long? Eip145Transition { get; set; }

        public long? Eip1014Transition { get; set; }

        public long? Eip1052Transition { get; set; }

        public long? Eip1108Transition { get; set; }

        public long? Eip1283Transition { get; set; }

        public long? Eip1283DisableTransition { get; set; }

        public long? Eip1283ReenableTransition { get; set; }

        public long? Eip1344Transition { get; set; }

        public long? Eip1706Transition { get; set; }

        public long? Eip1884Transition { get; set; }

        public long? Eip2028Transition { get; set; }

        public long? Eip2200Transition { get; set; }

        public long? Eip1559Transition { get; set; }

        public long? Eip2315Transition { get; set; }

        public long? Eip2537Transition { get; set; }

        public long? Eip2565Transition { get; set; }

        public long? Eip2929Transition { get; set; }

        public long? Eip2930Transition { get; set; }

        public long? Eip3198Transition { get; set; }

        public long? Eip3529Transition { get; set; }

        public long? Eip3541Transition { get; set; }

        // We explicitly want this to be enabled by default on all the networks
        // we can disable it if needed, but its expected not to cause issues
        public long? Eip3607Transition { get; set; } = 0;

        public UInt256? Eip1559BaseFeeInitialValue { get; set; }

        public UInt256? Eip1559BaseFeeMaxChangeDenominator { get; set; }

        public long? Eip1559ElasticityMultiplier { get; set; }

        public Address TransactionPermissionContract { get; set; }

        public long? TransactionPermissionContractTransition { get; set; }

        public long? ValidateChainIdTransition { get; set; }

        public long? ValidateReceiptsTransition { get; set; }

        public long? Eip1559FeeCollectorTransition { get; set; }

        public Address Eip1559FeeCollector { get; set; }

        public long? Eip1559BaseFeeMinValueTransition { get; set; }

        public UInt256? Eip1559BaseFeeMinValue { get; set; }

        public long? MergeForkIdTransition { get; set; }

        public UInt256? TerminalTotalDifficulty { get; set; }

        public long? TerminalPoWBlockNumber { get; set; }

        public ulong? Eip1153TransitionTimestamp { get; set; }

        public ulong? Eip3651TransitionTimestamp { get; set; }
        public ulong? Eip3855TransitionTimestamp { get; set; }
        public ulong? Eip3860TransitionTimestamp { get; set; }
        public ulong? Eip4895TransitionTimestamp { get; set; }

        public ulong? Eip4844TransitionTimestamp { get; set; }
    }
}
