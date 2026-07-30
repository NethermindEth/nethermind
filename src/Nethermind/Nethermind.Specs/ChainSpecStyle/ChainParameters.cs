// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.Specs.ChainSpecStyle;

public class ChainParameters
{
    public long? MaxCodeSize { get; set; }
    public ulong? MaxCodeSizeTransition { get; set; }
    public ulong? MaxCodeSizeTransitionTimestamp { get; set; }
    public ulong GasLimitBoundDivisor { get; set; }
    public Address Registrar { get; set; }
    public long MaximumExtraDataSize { get; set; }
    public ulong MinGasLimit { get; set; }
    public ulong MinHistoryRetentionEpochs { get; set; }
    public ulong MinBalRetentionEpochs { get; set; }
    public Hash256 ForkCanonHash { get; set; }
    public ulong? ForkBlock { get; set; }
    public ulong? Eip7Transition { get; set; }
    public ulong? Eip150Transition { get; set; }
    public ulong? Eip152Transition { get; set; }
    public ulong? Eip160Transition { get; set; }
    public ulong? Eip161abcTransition { get; set; }
    public ulong? Eip161dTransition { get; set; }
    public ulong? Eip155Transition { get; set; }
    public ulong? Eip140Transition { get; set; }
    public ulong? Eip211Transition { get; set; }
    public ulong? Eip214Transition { get; set; }
    public ulong? Eip658Transition { get; set; }
    public ulong? Eip145Transition { get; set; }
    public ulong? Eip1014Transition { get; set; }
    public ulong? Eip1052Transition { get; set; }
    public ulong? Eip1108Transition { get; set; }
    public ulong? Eip1283Transition { get; set; }
    public ulong? Eip1283DisableTransition { get; set; }
    public ulong? Eip1283ReenableTransition { get; set; }
    public ulong? Eip1344Transition { get; set; }
    public ulong? Eip1706Transition { get; set; }
    public ulong? Eip1884Transition { get; set; }
    public ulong? Eip2028Transition { get; set; }
    public ulong? Eip2200Transition { get; set; }
    public ulong? Eip1559Transition { get; set; }
    public ulong? Eip2315Transition { get; set; }
    public ulong? Eip2537Transition { get; set; }
    public ulong? Eip2537TransitionTimestamp { get; set; }
    public ulong? Eip2565Transition { get; set; }
    public ulong? Eip2929Transition { get; set; }
    public ulong? Eip2930Transition { get; set; }
    public ulong? Eip3198Transition { get; set; }
    public ulong? Eip3529Transition { get; set; }

    public ulong? Eip3541Transition { get; set; }
    public ulong? Eip3607Transition { get; set; }

    public UInt256? Eip1559BaseFeeInitialValue { get; set; }

    public UInt256? Eip1559BaseFeeMaxChangeDenominator { get; set; }

    public ulong? Eip1559ElasticityMultiplier { get; set; }

    /// <summary>
    ///  Transaction permission managing contract address.
    /// </summary>
    public Address TransactionPermissionContract { get; set; }
    /// <summary>
    /// Block at which the transaction permission contract should start being used.
    /// </summary>
    public ulong? TransactionPermissionContractTransition { get; set; }

    /// <summary>
    /// Optional, will be included for block 0 by default - Block before which any chain_id in the signature of a replay-protected transaction is accepted.
    /// After this transition block, the transactions' chain_id must match with the spec chain_id to be considered valid.
    /// </summary>
    /// <remarks>Backward compatibility for early Kovan blocks.</remarks>
    public ulong? ValidateChainIdTransition { get; set; }

    /// <summary>
    /// Optional, will be included for block 0 by default - Transition block before which the state root in transaction's receipt can be stripped.
    /// </summary>
    /// <returns></returns>
    public ulong? ValidateReceiptsTransition { get; set; }

    /// <summary>
    /// Block from which burnt EIP-1559 fees will go to <see cref="Eip1559FeeCollector"/>
    /// </summary>
    public ulong? Eip1559FeeCollectorTransition { get; set; }

    /// <summary>
    /// Optional, address where burnt EIP-1559 fees will go
    /// </summary>
    public Address FeeCollector { get; set; }

    /// <summary>
    /// Block from which EIP1559 base fee cannot drop below <see cref="Eip1559BaseFeeMinValue"/>
    /// </summary>
    public ulong? Eip1559BaseFeeMinValueTransition { get; set; }

    /// <summary>
    /// Optional, minimal value of EIP1559 base fee
    /// </summary>
    public UInt256? Eip1559BaseFeeMinValue { get; set; }

    public ulong? MergeForkIdTransition { get; set; }

    public ulong? TerminalPoWBlockNumber { get; set; }

    public UInt256? TerminalTotalDifficulty { get; set; }
    public ulong? BeaconChainGenesisTimestamp { get; set; }
    public ulong? Eip3651Transition { get; set; }
    public ulong? Eip3651TransitionTimestamp { get; set; }
    public ulong? Eip3855Transition { get; set; }
    public ulong? Eip3855TransitionTimestamp { get; set; }
    public ulong? Eip3860Transition { get; set; }
    public ulong? Eip3860TransitionTimestamp { get; set; }
    public ulong? Eip4895TransitionTimestamp { get; set; }
    public ulong? Eip4844TransitionTimestamp { get; set; }
    public ulong? Eip4844Transition { get; set; }
    public ulong? Eip1153Transition { get; set; }
    public ulong? Eip1153TransitionTimestamp { get; set; }
    public ulong? Eip5656Transition { get; set; }
    public ulong? Eip5656TransitionTimestamp { get; set; }
    public ulong? Eip6780Transition { get; set; }
    public ulong? Eip6780TransitionTimestamp { get; set; }
    public ulong? Eip4788TransitionTimestamp { get; set; }
    public Address Eip4788ContractAddress { get; set; }
    public ulong? Eip6110TransitionTimestamp { get; set; }
    public Address DepositContractAddress { get; set; }
    public ulong? Eip7002TransitionTimestamp { get; set; }
    public Address Eip7002ContractAddress { get; set; }
    public ulong? Eip7251TransitionTimestamp { get; set; }
    public Address Eip7251ContractAddress { get; set; }
    public ulong? Eip2935Transition { get; set; }
    public ulong? Eip2935TransitionTimestamp { get; set; }
    public Address Eip2935ContractAddress { get; set; }
    public ulong Eip2935RingBufferSize { get; set; } = Eip2935Constants.RingBufferSize;
    public ulong? Eip7951TransitionTimestamp { get; set; }
    public ulong? Rip7212TransitionTimestamp { get; set; }
    public ulong? Eip7702Transition { get; set; }
    public ulong? Eip7702TransitionTimestamp { get; set; }

    public ulong? Eip7594TransitionTimestamp { get; set; }
    public ulong? Eip7623Transition { get; set; }
    public ulong? Eip7623TransitionTimestamp { get; set; }
    public ulong? Eip7778TransitionTimestamp { get; set; }
    public ulong? Eip7823TransitionTimestamp { get; set; }
    public ulong? Eip7825TransitionTimestamp { get; set; }
    public ulong? Eip7883TransitionTimestamp { get; set; }
    public ulong? Eip7918TransitionTimestamp { get; set; }
    public ulong? Eip7976TransitionTimestamp { get; set; }
    public ulong? Eip7981TransitionTimestamp { get; set; }

    public ulong? Eip7934TransitionTimestamp { get; set; }
    public int Eip7934MaxRlpBlockSize { get; set; }

    public SortedSet<BlobScheduleSettings>? BlobSchedule { get; set; } = [];

    #region EIP-4844 parameters
    /// <summary>
    /// Gets or sets the <c>BLOB_GASPRICE_UPDATE_FRACTION</c> parameter defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4844#parameters">EIP-4844</see>.
    /// </summary>
    public ulong? Eip4844BlobGasPriceUpdateFraction { get; set; }

    /// <summary>
    /// Gets or sets the <c>MIN_BLOB_GASPRICE</c> parameter, in wei, defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4844#parameters">EIP-4844</see>.
    /// </summary>
    public UInt256? Eip4844MinBlobGasPrice { get; set; }

    /// <summary>
    /// Enables blob gas fee collection for Gnosis chain
    /// </summary>
    public ulong? Eip4844FeeCollectorTransitionTimestamp { get; set; }

    public ulong? Eip7939TransitionTimestamp { get; set; }

    #endregion

    public ulong? Eip8037TransitionTimestamp { get; set; }
    public ulong? Eip7928TransitionTimestamp { get; set; }

    public ulong? Eip7708TransitionTimestamp { get; set; }
    public ulong? Eip8024TransitionTimestamp { get; set; }
    public ulong? Eip8246TransitionTimestamp { get; set; }
    public ulong? Eip8038TransitionTimestamp { get; set; }
    public ulong? Eip8282TransitionTimestamp { get; set; }
    public ulong? Eip7843TransitionTimestamp { get; set; }
    public ulong? Eip7954TransitionTimestamp { get; set; }
    public ulong? Eip2780TransitionTimestamp { get; set; }
}
