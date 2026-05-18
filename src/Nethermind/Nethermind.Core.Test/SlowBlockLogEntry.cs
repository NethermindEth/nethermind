// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Core.Test;

/// <summary>
/// Strongly-typed model for deserializing slow block JSON logs in tests.
/// Must mirror the JSON structure written by ProcessingStats.LogSlowBlock().
/// </summary>
public record SlowBlockLogEntry
{
    [JsonPropertyName("level")] public string Level { get; init; } = string.Empty;
    [JsonPropertyName("msg")] public string Msg { get; init; } = string.Empty;
    [JsonPropertyName("block")] public BlockInfo Block { get; init; } = new();
    [JsonPropertyName("timing")] public TimingInfo Timing { get; init; } = new();
    [JsonPropertyName("throughput")] public ThroughputInfo Throughput { get; init; } = new();
    [JsonPropertyName("state_reads")] public StateReadsInfo StateReads { get; init; } = new();
    [JsonPropertyName("state_writes")] public StateWritesInfo StateWrites { get; init; } = new();
    [JsonPropertyName("cache")] public CacheInfo Cache { get; init; } = new();
    [JsonPropertyName("evm")] public EvmInfo Evm { get; init; } = new();

    public record BlockInfo
    {
        [JsonPropertyName("number")] public long Number { get; init; }
        [JsonPropertyName("hash")] public string Hash { get; init; } = string.Empty;
        [JsonPropertyName("gas_used")] public long GasUsed { get; init; }
        [JsonPropertyName("gas_limit")] public long GasLimit { get; init; }
        [JsonPropertyName("tx_count")] public int TxCount { get; init; }
        [JsonPropertyName("blob_count")] public int BlobCount { get; init; }
    }

    public record TimingInfo
    {
        [JsonPropertyName("execution_ms")] public double ExecutionMs { get; init; }
        [JsonPropertyName("evm_ms")] public double EvmMs { get; init; }
        [JsonPropertyName("blooms_ms")] public double BloomsMs { get; init; }
        [JsonPropertyName("receipts_root_ms")] public double ReceiptsRootMs { get; init; }
        [JsonPropertyName("commit_ms")] public double CommitMs { get; init; }
        [JsonPropertyName("storage_merkle_ms")] public double StorageMerkleMs { get; init; }
        [JsonPropertyName("state_root_ms")] public double StateRootMs { get; init; }
        [JsonPropertyName("state_hash_ms")] public double StateHashMs { get; init; }
        [JsonPropertyName("total_ms")] public double TotalMs { get; init; }
    }

    public record ThroughputInfo { [JsonPropertyName("mgas_per_sec")] public double MgasPerSec { get; init; } }

    public record StateReadsInfo
    {
        [JsonPropertyName("accounts")] public long Accounts { get; init; }
        [JsonPropertyName("storage_slots")] public long StorageSlots { get; init; }
        [JsonPropertyName("code")] public long Code { get; init; }
        [JsonPropertyName("code_bytes")] public long CodeBytes { get; init; }
    }

    public record StateWritesInfo
    {
        [JsonPropertyName("accounts")] public long Accounts { get; init; }
        [JsonPropertyName("accounts_deleted")] public long AccountsDeleted { get; init; }
        [JsonPropertyName("storage_slots")] public long StorageSlots { get; init; }
        [JsonPropertyName("storage_slots_deleted")] public long StorageSlotsDeleted { get; init; }
        [JsonPropertyName("code")] public long Code { get; init; }
        [JsonPropertyName("code_bytes")] public long CodeBytes { get; init; }
        [JsonPropertyName("eip7702_delegations_set")] public long Eip7702DelegationsSet { get; init; }
        [JsonPropertyName("eip7702_delegations_cleared")] public long Eip7702DelegationsCleared { get; init; }
    }

    public record CacheInfo
    {
        [JsonPropertyName("account")] public CacheEntryInfo Account { get; init; } = new();
        [JsonPropertyName("storage")] public CacheEntryInfo Storage { get; init; } = new();
        [JsonPropertyName("code")] public CacheEntryInfo Code { get; init; } = new();
    }

    public record CacheEntryInfo
    {
        [JsonPropertyName("hits")] public long Hits { get; init; }
        [JsonPropertyName("misses")] public long Misses { get; init; }
        [JsonPropertyName("hit_rate")] public double HitRate { get; init; }
    }

    public record EvmInfo
    {
        [JsonPropertyName("opcodes")] public long Opcodes { get; init; }
        [JsonPropertyName("sload")] public long Sload { get; init; }
        [JsonPropertyName("sstore")] public long Sstore { get; init; }
        [JsonPropertyName("calls")] public long Calls { get; init; }
        [JsonPropertyName("empty_calls")] public long EmptyCalls { get; init; }
        [JsonPropertyName("creates")] public long Creates { get; init; }
        [JsonPropertyName("self_destructs")] public long SelfDestructs { get; init; }
        [JsonPropertyName("contracts_analyzed")] public long ContractsAnalyzed { get; init; }
        [JsonPropertyName("cached_contracts_used")] public long CachedContractsUsed { get; init; }
    }
}
