// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

[assembly: InternalsVisibleTo("Nethermind.Specs.Test")]
[assembly: InternalsVisibleTo("Nethermind.TxPool.Test")]
namespace Nethermind.Specs.ChainSpecStyle.Json;

public class ChainSpecParamsJson : IHasNamedForks
{
    public ulong? ChainId { get; set; }
    public ulong? NetworkId { get; set; }

    public Address Registrar { get; set; }

    public long? GasLimitBoundDivisor { get; set; }

    public long? MaximumExtraDataSize { get; set; }

    public long? MinGasLimit { get; set; }

    public long? MinHistoryRetentionEpochs { get; set; }

    public long? MinBalRetentionEpochs { get; set; }

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

    /// <summary>
    /// Catch-all for top-level chainspec params keys that don't map to an explicit property —
    /// in practice the hardfork shorthand labels (<c>shanghai</c>, <c>cancun</c>, <c>prague</c>,
    /// <c>osaka</c>, <c>amsterdam</c>, <c>homestead</c>, <c>tangerineWhistle</c>,
    /// <c>spuriousDragon</c>, <c>byzantium</c>, <c>constantinople</c>, <c>petersburg</c>,
    /// <c>istanbul</c>, <c>berlin</c>, <c>london</c>). <see cref="HardforkLabels.ExpandAll"/>
    /// consumes each recognized entry and expands it into the per-EIP transition fields above;
    /// anything still present after expansion is an unknown/typo key.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? NamedForks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    private Dictionary<string, long>? _namedForkBlocks;

    [JsonIgnore]
    private Dictionary<string, ulong>? _namedForkTimestamps;

    IReadOnlyDictionary<string, long>? IHasNamedForks.NamedForkBlocks
        => _namedForkBlocks ??= Project<long>(HardforkLabelKind.Block);

    IReadOnlyDictionary<string, ulong>? IHasNamedForks.NamedForkTimestamps
        => _namedForkTimestamps ??= Project<ulong>(HardforkLabelKind.Timestamp);

    /// <summary>
    /// Parses the <c>[JsonExtensionData]</c> entries whose keys match a <see cref="HardforkLabels"/>
    /// label of the given <paramref name="kind"/> into a typed lookup.
    /// </summary>
    private Dictionary<string, T>? Project<T>(HardforkLabelKind kind) where T : struct
    {
        if (NamedForks is null or { Count: 0 }) return null;
        Dictionary<string, T>? result = null;
        foreach (IHardforkLabel label in HardforkLabels.All)
        {
            if (label.Kind == kind && NamedForks.TryGetValue(label.LabelName, out JsonElement element))
            {
                result ??= new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
                result[label.LabelName] = element.Deserialize<T>(EthereumJsonSerializer.JsonOptions);
            }
        }
        return result;
    }
}
