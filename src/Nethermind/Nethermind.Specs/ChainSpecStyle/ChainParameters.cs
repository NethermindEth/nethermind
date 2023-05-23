// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Specs.ChainSpecStyle
{
    public class ChainParameters
    {
        public long? MaxCodeSize { get; set; }
        public long? MaxCodeSizeTransition { get; set; }
        public ulong? MaxCodeSizeTransitionTimestamp { get; set; }
        public long GasLimitBoundDivisor { get; set; }
        public Address Registrar { get; set; }
        public long MaximumExtraDataSize { get; set; }
        public long MinGasLimit { get; set; }
        public Keccak ForkCanonHash { get; set; }
        public long? ForkBlock { get; set; }
        public long? Eip7Transition { get; set; }
        public long? Eip150Transition { get; set; }
        public long? Eip152Transition { get; set; }
        public long? Eip160Transition { get; set; }
        public long? Eip161abcTransition { get; set; }
        public long? Eip161dTransition { get; set; }
        public long? Eip155Transition { get; set; }
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
        public ulong? Eip2537TransitionTimestamp { get; set; }
        public long? Eip2565Transition { get; set; }
        public long? Eip2929Transition { get; set; }
        public long? Eip2930Transition { get; set; }
        public long? Eip3198Transition { get; set; }
        public long? Eip3529Transition { get; set; }

        public long? Eip3541Transition { get; set; }
        public long? Eip3607Transition { get; set; }

        public UInt256 Eip1559BaseFeeInitialValue { get; set; }

        public UInt256 Eip1559BaseFeeMaxChangeDenominator { get; set; }

        public long Eip1559ElasticityMultiplier { get; set; }

        /// <summary>
        ///  Transaction permission managing contract address.
        /// </summary>
        public Address TransactionPermissionContract { get; set; }
        /// <summary>
        /// Block at which the transaction permission contract should start being used.
        /// </summary>
        public long? TransactionPermissionContractTransition { get; set; }

        /// <summary>
        /// Optional, will be included for block 0 by default - Block before which any chain_id in the signature of a replay-protected transaction is accepted.
        /// After this transition block, the transactions’ chain_id must match with the spec chain_id to be considered valid.
        /// </summary>
        /// <remarks>Backward compatibility for early Kovan blocks.</remarks>
        public long? ValidateChainIdTransition { get; set; }

        /// <summary>
        /// Optional, will be included for block 0 by default - Transition block before which the state root in transaction’s receipt can be stripped.
        /// </summary>
        /// <returns></returns>
        public long? ValidateReceiptsTransition { get; set; }

        /// <summary>
        /// Block from which burnt EIP-1559 fees will go to <see cref="Eip1559FeeCollector"/>
        /// </summary>
        public long? Eip1559FeeCollectorTransition { get; set; }

        /// <summary>
        /// Optional, address where burnt EIP-1559 fees will go
        /// </summary>
        public Address Eip1559FeeCollector { get; set; }

        /// <summary>
        /// Block from which EIP1559 base fee cannot drop below <see cref="Eip1559BaseFeeMinValue"/>
        /// </summary>
        public long? Eip1559BaseFeeMinValueTransition { get; set; }

        /// <summary>
        /// Optional, minimal value of EIP1559 base fee
        /// </summary>
        public UInt256? Eip1559BaseFeeMinValue { get; set; }

        public long? MergeForkIdTransition { get; set; }

        public long? TerminalPowBlockNumber { get; set; }

        public UInt256? TerminalTotalDifficulty { get; set; }

        /// <summary>
        /// this field will indicate the timestamp at which this EIP1153 will be enabled.
        /// </summary>
        public ulong? Eip1153TransitionTimestamp { get; set; }

        /// <summary>
        /// this field will indicate the timestamp at which this EIP3651 will be enabled.
        /// </summary>
        public ulong? Eip3651TransitionTimestamp { get; set; }
        public ulong? Eip3855TransitionTimestamp { get; set; }
        public ulong? Eip3860TransitionTimestamp { get; set; }

        public ulong? Eip4895TransitionTimestamp { get; set; }

        public ulong? Eip4844TransitionTimestamp { get; set; }
    }
}
