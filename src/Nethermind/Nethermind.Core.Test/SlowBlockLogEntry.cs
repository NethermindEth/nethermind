// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Core.Test;

/// <summary>
/// Strongly-typed representation of slow block JSON log for test assertions.
/// Mirrors the JSON structure in ProcessingStats.LogSlowBlock().
/// </summary>
public record SlowBlockLogEntry
{
    [JsonPropertyName("level")]
    public string Level { get; init; } = string.Empty;

    [JsonPropertyName("msg")]
    public string Msg { get; init; } = string.Empty;

    [JsonPropertyName("block")]
    public SlowBlockInfo Block { get; init; } = new();

    [JsonPropertyName("timing")]
    public SlowBlockTiming Timing { get; init; } = new();

    [JsonPropertyName("throughput")]
    public SlowBlockThroughput Throughput { get; init; } = new();

    [JsonPropertyName("state_reads")]
    public SlowBlockStateReads StateReads { get; init; } = new();

    [JsonPropertyName("state_writes")]
    public SlowBlockStateWrites StateWrites { get; init; } = new();

    [JsonPropertyName("cache")]
    public SlowBlockCache Cache { get; init; } = new();

    [JsonPropertyName("evm")]
    public SlowBlockEvm Evm { get; init; } = new();
}

/// <summary>
/// Block identification information.
/// </summary>
public record SlowBlockInfo
{
    [JsonPropertyName("number")]
    public long Number { get; init; }

    [JsonPropertyName("hash")]
    public string Hash { get; init; } = string.Empty;

    [JsonPropertyName("gas_used")]
    public long GasUsed { get; init; }

    [JsonPropertyName("tx_count")]
    public int TxCount { get; init; }
}

/// <summary>
/// Timing breakdown for block processing.
/// </summary>
public record SlowBlockTiming
{
    [JsonPropertyName("execution_ms")]
    public double ExecutionMs { get; init; }

    [JsonPropertyName("state_read_ms")]
    public double StateReadMs { get; init; }

    [JsonPropertyName("state_hash_ms")]
    public double StateHashMs { get; init; }

    [JsonPropertyName("commit_ms")]
    public double CommitMs { get; init; }

    [JsonPropertyName("total_ms")]
    public double TotalMs { get; init; }
}

/// <summary>
/// Throughput metrics.
/// </summary>
public record SlowBlockThroughput
{
    [JsonPropertyName("mgas_per_sec")]
    public double MgasPerSec { get; init; }
}

/// <summary>
/// State read operation counts.
/// </summary>
public record SlowBlockStateReads
{
    [JsonPropertyName("accounts")]
    public long Accounts { get; init; }

    [JsonPropertyName("storage_slots")]
    public long StorageSlots { get; init; }

    [JsonPropertyName("code")]
    public long Code { get; init; }

    [JsonPropertyName("code_bytes")]
    public long CodeBytes { get; init; }
}

/// <summary>
/// State write operation counts including EIP-7702 delegation tracking.
/// </summary>
public record SlowBlockStateWrites
{
    [JsonPropertyName("accounts")]
    public long Accounts { get; init; }

    [JsonPropertyName("accounts_deleted")]
    public long AccountsDeleted { get; init; }

    [JsonPropertyName("storage_slots")]
    public long StorageSlots { get; init; }

    [JsonPropertyName("storage_slots_deleted")]
    public long StorageSlotsDeleted { get; init; }

    [JsonPropertyName("code")]
    public long Code { get; init; }

    [JsonPropertyName("code_bytes")]
    public long CodeBytes { get; init; }

    [JsonPropertyName("eip7702_delegations_set")]
    public long Eip7702DelegationsSet { get; init; }

    [JsonPropertyName("eip7702_delegations_cleared")]
    public long Eip7702DelegationsCleared { get; init; }
}

/// <summary>
/// Cache statistics for account, storage, and code caches.
/// </summary>
public record SlowBlockCache
{
    [JsonPropertyName("account")]
    public SlowBlockCacheStats Account { get; init; } = new();

    [JsonPropertyName("storage")]
    public SlowBlockCacheStats Storage { get; init; } = new();

    [JsonPropertyName("code")]
    public SlowBlockCacheStats Code { get; init; } = new();
}

/// <summary>
/// Cache hit/miss statistics for a single cache type.
/// </summary>
public record SlowBlockCacheStats
{
    [JsonPropertyName("hits")]
    public long Hits { get; init; }

    [JsonPropertyName("misses")]
    public long Misses { get; init; }

    [JsonPropertyName("hit_rate")]
    public double HitRate { get; init; }
}

/// <summary>
/// EVM operation counts (Nethermind extension beyond Geth's metrics).
/// </summary>
public record SlowBlockEvm
{
    [JsonPropertyName("sload")]
    public long Sload { get; init; }

    [JsonPropertyName("sstore")]
    public long Sstore { get; init; }

    [JsonPropertyName("calls")]
    public long Calls { get; init; }

    [JsonPropertyName("creates")]
    public long Creates { get; init; }
}
