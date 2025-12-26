// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Specs.ChainSpecStyle.Json;

/// <summary>
/// Represents the config object in a Geth-style genesis.json file as defined in EIP-7949.
/// </summary>
public class GethGenesisConfigJson
{
    public ulong ChainId { get; set; }

    public long? HomesteadBlock { get; set; }

    public long? DaoForkBlock { get; set; }

    public bool? DaoForkSupport { get; set; }

    [JsonPropertyName("eip150Block")]
    public long? Eip150Block { get; set; }

    [JsonPropertyName("eip155Block")]
    public long? Eip155Block { get; set; }

    [JsonPropertyName("eip158Block")]
    public long? Eip158Block { get; set; }

    [JsonPropertyName("tangerineWhistleBlock")]
    public long? TangerineWhistleBlock { get; set; }

    [JsonPropertyName("spuriousDragonBlock")]
    public long? SpuriousDragonBlock { get; set; }

    public long? ByzantiumBlock { get; set; }

    public long? ConstantinopleBlock { get; set; }

    public long? PetersburgBlock { get; set; }

    public long? IstanbulBlock { get; set; }

    public long? MuirGlacierBlock { get; set; }

    public long? BerlinBlock { get; set; }

    public long? LondonBlock { get; set; }

    public long? ArrowGlacierBlock { get; set; }

    public long? GrayGlacierBlock { get; set; }

    public long? MergeNetsplitBlock { get; set; }

    public ulong? ShanghaiTime { get; set; }

    public ulong? CancunTime { get; set; }

    public ulong? PragueTime { get; set; }

    public ulong? OsakaTime { get; set; }

    /// <summary>
    /// The terminal total difficulty for the merge.
    /// This is a hex string as defined in EIP-7949.
    /// </summary>
    public string TerminalTotalDifficulty { get; set; }

    public bool? TerminalTotalDifficultyPassed { get; set; }

    public Address DepositContractAddress { get; set; }

    /// <summary>
    /// The blob schedule mapping hardforks to their EIP-4844 DAS configuration parameters.
    /// </summary>
    public Dictionary<string, GethBlobScheduleEntry> BlobSchedule { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; }
}

/// <summary>
/// Represents a blob schedule entry in the EIP-7949 format.
/// Extended to support explicit timestamps for custom hardforks.
/// </summary>
public class GethBlobScheduleEntry
{
    public ulong Target { get; set; }

    public ulong Max { get; set; }

    public ulong BaseFeeUpdateFraction { get; set; }

    /// <summary>
    /// Explicit timestamp for custom hardforks that don't have a corresponding hardfork time in config.
    /// This is an extension to the EIP-7949 format for practical use.
    /// </summary>
    public ulong? Timestamp { get; set; }
}
