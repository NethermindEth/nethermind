// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Extensions;

namespace Nethermind.Db.Rocks.Config;

public class DbConfig : IDbConfig
{
    public static DbConfig Default = new DbConfig();

    public ulong SharedBlockCacheSize { get; set; } = (ulong)256.MiB();
    public bool SkipMemoryHintSetting { get; set; } = false;

    public ulong WriteBufferSize { get; set; } = (ulong)16.MiB();
    public uint WriteBufferNumber { get; set; } = 4;
    public ulong BlockCacheSize { get; set; } = 0;
    public bool CacheIndexAndFilterBlocks { get; set; } = false;
    public int? MaxOpenFiles { get; set; }
    public long? MaxBytesPerSec { get; set; }
    public int? BlockSize { get; set; } = 16 * 1024;
    public IDictionary<string, string>? AdditionalRocksDbOptions { get; set; }

    public ulong ReceiptsDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint ReceiptsDbWriteBufferNumber { get; set; } = 4;
    public ulong ReceiptsDbBlockCacheSize { get; set; } = 0;
    public bool ReceiptsDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? ReceiptsDbMaxOpenFiles { get; set; }
    public long? ReceiptsDbMaxBytesPerSec { get; set; }
    public int? ReceiptsBlockSize { get; set; }
    public IDictionary<string, string>? ReceiptsDbAdditionalRocksDbOptions { get; set; }

    public ulong BlocksDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint BlocksDbWriteBufferNumber { get; set; } = 4;
    public ulong BlocksDbBlockCacheSize { get; set; } = 0;
    public bool BlocksDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? BlocksDbMaxOpenFiles { get; set; }
    public long? BlocksDbMaxBytesPerSec { get; set; }
    public int? BlocksBlockSize { get; set; }
    public IDictionary<string, string>? BlocksDbAdditionalRocksDbOptions { get; set; }

    public ulong HeadersDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint HeadersDbWriteBufferNumber { get; set; } = 4;
    public ulong HeadersDbBlockCacheSize { get; set; } = 0;
    public bool HeadersDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? HeadersDbMaxOpenFiles { get; set; }
    public long? HeadersDbMaxBytesPerSec { get; set; }
    public int? HeadersBlockSize { get; set; } = 4 * 1024;
    public IDictionary<string, string>? HeadersDbAdditionalRocksDbOptions { get; set; }

    public ulong BlockInfosDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint BlockInfosDbWriteBufferNumber { get; set; } = 4;
    public ulong BlockInfosDbBlockCacheSize { get; set; } = 0;
    public bool BlockInfosDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? BlockInfosDbMaxOpenFiles { get; set; }
    public long? BlockInfosDbMaxBytesPerSec { get; set; }
    public int? BlockInfosBlockSize { get; set; }
    public IDictionary<string, string>? BlockInfosDbAdditionalRocksDbOptions { get; set; }

    public ulong PendingTxsDbWriteBufferSize { get; set; } = (ulong)4.MiB();
    public uint PendingTxsDbWriteBufferNumber { get; set; } = 4;
    public ulong PendingTxsDbBlockCacheSize { get; set; } = 0;
    public bool PendingTxsDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? PendingTxsDbMaxOpenFiles { get; set; }
    public long? PendingTxsDbMaxBytesPerSec { get; set; }
    public int? PendingTxsBlockSize { get; set; }
    public IDictionary<string, string>? PendingTxsDbAdditionalRocksDbOptions { get; set; }

    public ulong CodeDbWriteBufferSize { get; set; } = (ulong)2.MiB();
    public uint CodeDbWriteBufferNumber { get; set; } = 4;
    public ulong CodeDbBlockCacheSize { get; set; } = 0;
    public bool CodeDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? CodeDbMaxOpenFiles { get; set; }
    public long? CodeDbMaxBytesPerSec { get; set; }
    public int? CodeBlockSize { get; set; }
    public IDictionary<string, string>? CodeDbAdditionalRocksDbOptions { get; set; }

    public ulong BloomDbWriteBufferSize { get; set; } = (ulong)1.KiB();
    public uint BloomDbWriteBufferNumber { get; set; } = 4;
    public ulong BloomDbBlockCacheSize { get; set; } = 0;
    public bool BloomDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? BloomDbMaxOpenFiles { get; set; }
    public long? BloomDbMaxBytesPerSec { get; set; }
    public IDictionary<string, string>? BloomDbAdditionalRocksDbOptions { get; set; }

    public ulong WitnessDbWriteBufferSize { get; set; } = (ulong)1.KiB();
    public uint WitnessDbWriteBufferNumber { get; set; } = 4;
    public ulong WitnessDbBlockCacheSize { get; set; } = 0;
    public bool WitnessDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? WitnessDbMaxOpenFiles { get; set; }
    public long? WitnessDbMaxBytesPerSec { get; set; }
    public int? WitnessBlockSize { get; set; }
    public IDictionary<string, string>? WitnessDbAdditionalRocksDbOptions { get; set; }

    // TODO - profile and customize
    public ulong CanonicalHashTrieDbWriteBufferSize { get; set; } = (ulong)2.MB();
    public uint CanonicalHashTrieDbWriteBufferNumber { get; set; } = 4;
    public ulong CanonicalHashTrieDbBlockCacheSize { get; set; } = 0;
    public bool CanonicalHashTrieDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? CanonicalHashTrieDbMaxOpenFiles { get; set; }
    public long? CanonicalHashTrieDbMaxBytesPerSec { get; set; }
    public int? CanonicalHashTrieBlockSize { get; set; }
    public IDictionary<string, string>? CanonicalHashTrieDbAdditionalRocksDbOptions { get; set; }

    public ulong MetadataDbWriteBufferSize { get; set; } = (ulong)1.KiB();
    public uint MetadataDbWriteBufferNumber { get; set; } = 4;
    public ulong MetadataDbBlockCacheSize { get; set; } = 0;
    public bool MetadataDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? MetadataDbMaxOpenFiles { get; set; }
    public long? MetadataDbMaxBytesPerSec { get; set; }
    public int? MetadataBlockSize { get; set; }
    public IDictionary<string, string>? MetadataDbAdditionalRocksDbOptions { get; set; }

    public ulong StateDbWriteBufferSize { get; set; }
    public uint StateDbWriteBufferNumber { get; set; }
    public ulong StateDbBlockCacheSize { get; set; }
    public bool StateDbCacheIndexAndFilterBlocks { get; set; }
    public int? StateDbMaxOpenFiles { get; set; }
    public long? StateDbMaxBytesPerSec { get; set; }
    public int? StateDbBlockSize { get; set; } = 4 * 1024;
    public IDictionary<string, string>? StateDbAdditionalRocksDbOptions { get; set; }

    public uint RecycleLogFileNum { get; set; } = 0;
    public bool WriteAheadLogSync { get; set; } = false;

    public bool EnableDbStatistics { get; set; } = false;
    public bool EnableMetricsUpdater { get; set; } = false;
    public uint StatsDumpPeriodSec { get; set; } = 600;
}
