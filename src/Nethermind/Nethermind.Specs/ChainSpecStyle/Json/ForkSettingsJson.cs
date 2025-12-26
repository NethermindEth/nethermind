// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Specs.ChainSpecStyle.Json;

/// <summary>
/// Represents default fork settings that define which EIPs are activated at each fork,
/// default blob schedules, and standard contract addresses.
/// </summary>
public class ForkSettingsJson
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("forks")]
    public Dictionary<string, ForkDefinitionJson>? Forks { get; set; }

    [JsonPropertyName("contracts")]
    public ContractAddressesJson? Contracts { get; set; }

    [JsonPropertyName("defaults")]
    public DefaultParametersJson? Defaults { get; set; }
}

/// <summary>
/// Defines a single fork with its activated EIPs and optional blob schedule.
/// </summary>
public class ForkDefinitionJson
{
    /// <summary>
    /// List of EIP numbers activated at this fork.
    /// </summary>
    [JsonPropertyName("eips")]
    public List<int>? Eips { get; set; }

    /// <summary>
    /// Blob schedule settings for this fork (if applicable).
    /// </summary>
    [JsonPropertyName("blobSchedule")]
    public BlobScheduleEntryJson? BlobSchedule { get; set; }

    /// <summary>
    /// Description of this fork.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// Blob schedule entry for a specific fork.
/// </summary>
public class BlobScheduleEntryJson
{
    [JsonPropertyName("target")]
    public int Target { get; set; }

    [JsonPropertyName("max")]
    public int Max { get; set; }

    [JsonPropertyName("baseFeeUpdateFraction")]
    public ulong BaseFeeUpdateFraction { get; set; }
}

/// <summary>
/// Standard contract addresses used across forks.
/// </summary>
public class ContractAddressesJson
{
    /// <summary>
    /// EIP-4788 Beacon block root contract address.
    /// </summary>
    [JsonPropertyName("beaconRoots")]
    public Address? BeaconRoots { get; set; }

    /// <summary>
    /// EIP-2935 Block hash history contract address.
    /// </summary>
    [JsonPropertyName("blockHashHistory")]
    public Address? BlockHashHistory { get; set; }

    /// <summary>
    /// EIP-7002 Withdrawal request predeploy address.
    /// </summary>
    [JsonPropertyName("withdrawalRequest")]
    public Address? WithdrawalRequest { get; set; }

    /// <summary>
    /// EIP-7251 Consolidation request predeploy address.
    /// </summary>
    [JsonPropertyName("consolidationRequest")]
    public Address? ConsolidationRequest { get; set; }

    /// <summary>
    /// Default deposit contract address.
    /// </summary>
    [JsonPropertyName("depositContract")]
    public Address? DepositContract { get; set; }
}

/// <summary>
/// Default parameters that apply to all chains.
/// </summary>
public class DefaultParametersJson
{
    [JsonPropertyName("gasLimitBoundDivisor")]
    public long GasLimitBoundDivisor { get; set; } = 0x400;

    [JsonPropertyName("maximumExtraDataSize")]
    public long MaximumExtraDataSize { get; set; } = 32;

    [JsonPropertyName("minGasLimit")]
    public long MinGasLimit { get; set; } = 5000;

    [JsonPropertyName("maxCodeSize")]
    public long MaxCodeSize { get; set; } = 0x6000;

    [JsonPropertyName("minHistoryRetentionEpochs")]
    public long MinHistoryRetentionEpochs { get; set; } = 82125;

    [JsonPropertyName("maxRlpBlockSize")]
    public int MaxRlpBlockSize { get; set; } = 8_388_608;
}
