// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("Nethermind.Specs.Test")]
[assembly: InternalsVisibleTo("Nethermind.TxPool.Test")]
namespace Nethermind.Specs.ChainSpecStyle.Json;

public class ChainSpecParamsJson
{
    public ulong? ChainId { get; set; }
    public ulong? NetworkId { get; set; }

    public Address Registrar { get; set; }

    public long? GasLimitBoundDivisor { get; set; }

    public long? MaximumExtraDataSize { get; set; }

    public long? MinGasLimit { get; set; }

    public long? MinHistoryRetentionEpochs { get; set; }

    public long? ForkBlock { get; set; }

    public Hash256 ForkCanonHash { get; set; }

    public long? Eip7Transition { get; set; }

    public long? Eip150Transition { get; set; }

    public long? Eip152Transition { get; set; }

    public long? Eip160Transition { get; set; }

    public long? Eip161abcTransition { get; set; }

    public long? Eip161dTransition { get; set; }

    public long? Eip155Transition { get; set; }

    public long? MaxCodeSize { get; set; }

    public long? MaxCodeSizeTransition { get; set; }

    public ulong? MaxCodeSizeTransitionTimestamp { get; set; }

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

    public Address FeeCollector { get; set; }

    public long? Eip1559BaseFeeMinValueTransition { get; set; }

    public UInt256? Eip1559BaseFeeMinValue { get; set; }

    public long? MergeForkIdTransition { get; set; }

    public UInt256? TerminalTotalDifficulty { get; set; }

    public long? TerminalPoWBlockNumber { get; set; }
    public ulong? BeaconChainGenesisTimestamp { get; set; }

    public long? Eip1153Transition { get; set; }
    public ulong? Eip1153TransitionTimestamp { get; set; }
    public long? Eip3651Transition { get; set; }
    public ulong? Eip3651TransitionTimestamp { get; set; }
    public long? Eip3855Transition { get; set; }
    public ulong? Eip3855TransitionTimestamp { get; set; }
    public long? Eip3860Transition { get; set; }
    public ulong? Eip3860TransitionTimestamp { get; set; }
    public ulong? Eip4895TransitionTimestamp { get; set; }
    public long? Eip4844Transition { get; set; }
    public ulong? Eip4844TransitionTimestamp { get; set; }
    public ulong? Eip2537TransitionTimestamp { get; set; }
    public long? Eip5656Transition { get; set; }
    public ulong? Eip5656TransitionTimestamp { get; set; }
    public long? Eip6780Transition { get; set; }
    public ulong? Eip6780TransitionTimestamp { get; set; }
    public ulong? Eip4788TransitionTimestamp { get; set; }
    public Address Eip4788ContractAddress { get; set; }
    public ulong? Eip2935TransitionTimestamp { get; set; }
    public Address Eip2935ContractAddress { get; set; }
    public long? Eip2935RingBufferSize { get; set; }
    public UInt256? Eip4844BlobGasPriceUpdateFraction { get; set; }
    public UInt256? Eip4844MinBlobGasPrice { get; set; }
    public ulong? Eip4844FeeCollectorTransitionTimestamp { get; set; }
    public ulong? Eip6110TransitionTimestamp { get; set; }
    public Address DepositContractAddress { get; set; }
    public ulong? Eip7002TransitionTimestamp { get; set; }
    public ulong? Eip7623TransitionTimestamp { get; set; }
    public ulong? Eip7976TransitionTimestamp { get; set; }
    public ulong? Eip7981TransitionTimestamp { get; set; }
    public Address Eip7002ContractAddress { get; set; }
    public ulong? Eip7251TransitionTimestamp { get; set; }
    public Address Eip7251ContractAddress { get; set; }
    public ulong? Eip7951TransitionTimestamp { get; set; }
    public ulong? Rip7212TransitionTimestamp { get; set; }
    public ulong? Eip7702TransitionTimestamp { get; set; }
    public ulong? Eip7883TransitionTimestamp { get; set; }
    public ulong? Eip7823TransitionTimestamp { get; set; }
    public ulong? Eip7825TransitionTimestamp { get; set; }
    public ulong? Eip7918TransitionTimestamp { get; set; }
    public ulong? Eip7934TransitionTimestamp { get; set; }
    public int? Eip7934MaxRlpBlockSize { get; set; }

    public ulong? OpGraniteTransitionTimestamp { get; set; }
    public ulong? OpHoloceneTransitionTimestamp { get; set; }
    public ulong? OpIsthmusTransitionTimestamp { get; set; }
    public SortedSet<BlobScheduleSettings> BlobSchedule { get; set; } = [];
    public ulong? Eip7594TransitionTimestamp { get; set; }
    public ulong? Eip7939TransitionTimestamp { get; set; }
    public ulong? Eip8037TransitionTimestamp { get; set; }
    public ulong? Eip7778TransitionTimestamp { get; set; }

    public ulong? Eip7928TransitionTimestamp { get; set; }
    public ulong? Eip7708TransitionTimestamp { get; set; }
    public ulong? Eip8024TransitionTimestamp { get; set; }
    public ulong? Eip7843TransitionTimestamp { get; set; }
    public ulong? Eip7954TransitionTimestamp { get; set; }

    /// <summary>Shorthand activation block for the full Homestead hardfork.</summary>
    /// <remarks>
    /// Expands to <see cref="Eip7Transition"/>. If this label and an explicit constituent both
    /// specify different values the chainspec is rejected at load. See <see cref="HardforkLabels"/>
    /// for the canonical fork-to-EIP mapping.
    /// </remarks>
    public long? Homestead { get; set; }

    /// <summary>Shorthand activation block for the full Tangerine Whistle hardfork.</summary>
    /// <remarks>Expands to <see cref="Eip150Transition"/>. <inheritdoc cref="Homestead"/></remarks>
    public long? TangerineWhistle { get; set; }

    /// <summary>Shorthand activation block for the full Spurious Dragon hardfork.</summary>
    /// <remarks>
    /// Expands to <see cref="Eip155Transition"/>, <see cref="Eip160Transition"/>,
    /// <see cref="Eip161abcTransition"/>, <see cref="Eip161dTransition"/>.
    /// <inheritdoc cref="Homestead"/>
    /// </remarks>
    public long? SpuriousDragon { get; set; }

    /// <summary>Shorthand activation block for the full Byzantium hardfork.</summary>
    /// <remarks>
    /// Expands to <see cref="Eip140Transition"/>, <see cref="Eip211Transition"/>,
    /// <see cref="Eip214Transition"/>, <see cref="Eip658Transition"/>.
    /// <inheritdoc cref="Homestead"/>
    /// </remarks>
    public long? Byzantium { get; set; }

    /// <summary>Shorthand activation block for the full Constantinople hardfork.</summary>
    /// <remarks>
    /// Expands to <see cref="Eip145Transition"/>, <see cref="Eip1014Transition"/>,
    /// <see cref="Eip1052Transition"/>, <see cref="Eip1283Transition"/>.
    /// <inheritdoc cref="Homestead"/>
    /// </remarks>
    public long? Constantinople { get; set; }

    /// <summary>Shorthand activation block for the Constantinople-fix / Petersburg hardfork (EIP-1283 disable).</summary>
    /// <remarks>Expands to <see cref="Eip1283DisableTransition"/>. <inheritdoc cref="Homestead"/></remarks>
    public long? ConstantinopleFix { get; set; }

    /// <summary>Shorthand activation block for the full Istanbul hardfork.</summary>
    /// <remarks>
    /// Expands to <see cref="Eip152Transition"/>, <see cref="Eip1108Transition"/>,
    /// <see cref="Eip1344Transition"/>, <see cref="Eip1884Transition"/>,
    /// <see cref="Eip2028Transition"/>, <see cref="Eip2200Transition"/>.
    /// <inheritdoc cref="Homestead"/>
    /// </remarks>
    public long? Istanbul { get; set; }

    /// <summary>Shorthand activation block for the full Berlin hardfork.</summary>
    /// <remarks>
    /// Expands to <see cref="Eip2565Transition"/>, <see cref="Eip2929Transition"/>,
    /// <see cref="Eip2930Transition"/>.
    /// <inheritdoc cref="Homestead"/>
    /// </remarks>
    public long? Berlin { get; set; }

    /// <summary>Shorthand activation block for the full London hardfork.</summary>
    /// <remarks>
    /// Expands to <see cref="Eip1559Transition"/>, <see cref="Eip3198Transition"/>,
    /// <see cref="Eip3529Transition"/>, <see cref="Eip3541Transition"/>.
    /// <inheritdoc cref="Homestead"/>
    /// </remarks>
    public long? London { get; set; }

    /// <summary>Shorthand activation timestamp for the full Shanghai hardfork.</summary>
    /// <remarks>
    /// Expands to <see cref="Eip3651TransitionTimestamp"/>, <see cref="Eip3855TransitionTimestamp"/>,
    /// <see cref="Eip3860TransitionTimestamp"/>, <see cref="Eip4895TransitionTimestamp"/>. If this
    /// label and an explicit constituent both specify different values the chainspec is rejected
    /// at load. See <see cref="HardforkLabels"/> for the canonical fork-to-EIP mapping.
    /// </remarks>
    public ulong? Shanghai { get; set; }

    /// <summary>Shorthand activation timestamp for the full Cancun hardfork.</summary>
    /// <remarks>
    /// Expands to <see cref="Eip1153TransitionTimestamp"/>, <see cref="Eip4788TransitionTimestamp"/>,
    /// <see cref="Eip4844TransitionTimestamp"/>, <see cref="Eip5656TransitionTimestamp"/>,
    /// <see cref="Eip6780TransitionTimestamp"/>.
    /// <inheritdoc cref="Shanghai"/>
    /// </remarks>
    public ulong? Cancun { get; set; }

    /// <summary>Shorthand activation timestamp for the full Prague hardfork.</summary>
    /// <remarks>
    /// Expands to <see cref="Eip2537TransitionTimestamp"/>, <see cref="Eip2935TransitionTimestamp"/>,
    /// <see cref="Eip6110TransitionTimestamp"/>, <see cref="Eip7002TransitionTimestamp"/>,
    /// <see cref="Eip7251TransitionTimestamp"/>, <see cref="Eip7623TransitionTimestamp"/>,
    /// <see cref="Eip7702TransitionTimestamp"/>.
    /// <inheritdoc cref="Shanghai"/>
    /// </remarks>
    public ulong? Prague { get; set; }

    /// <summary>Shorthand activation timestamp for the full Osaka hardfork.</summary>
    /// <remarks>
    /// Expands to <see cref="Eip7594TransitionTimestamp"/>, <see cref="Eip7823TransitionTimestamp"/>,
    /// <see cref="Eip7825TransitionTimestamp"/>, <see cref="Eip7883TransitionTimestamp"/>,
    /// <see cref="Eip7918TransitionTimestamp"/>, <see cref="Eip7934TransitionTimestamp"/>,
    /// <see cref="Eip7939TransitionTimestamp"/>, <see cref="Eip7951TransitionTimestamp"/>.
    /// <inheritdoc cref="Shanghai"/>
    /// </remarks>
    public ulong? Osaka { get; set; }

    /// <summary>Shorthand activation timestamp for the full Amsterdam hardfork.</summary>
    /// <remarks>
    /// Expands to <see cref="Eip7708TransitionTimestamp"/>, <see cref="Eip7778TransitionTimestamp"/>,
    /// <see cref="Eip7843TransitionTimestamp"/>, <see cref="Eip7928TransitionTimestamp"/>,
    /// <see cref="Eip7954TransitionTimestamp"/>, <see cref="Eip7976TransitionTimestamp"/>,
    /// <see cref="Eip7981TransitionTimestamp"/>, <see cref="Eip8024TransitionTimestamp"/>,
    /// <see cref="Eip8037TransitionTimestamp"/>.
    /// <inheritdoc cref="Shanghai"/>
    /// </remarks>
    public ulong? Amsterdam { get; set; }
}
