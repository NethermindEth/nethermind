// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Db.Rocks.Config;

public class DbConfig : IDbConfig
{
    public static DbConfig Default = new DbConfig();

    public ulong WriteBufferSize { get; set; } = (ulong)16.MiB();
    public uint WriteBufferNumber { get; set; } = 4;
    public ulong BlockCacheSize { get; set; } = (ulong)64.MiB();
    public bool CacheIndexAndFilterBlocks { get; set; } = false;
    public int? MaxOpenFiles { get; set; }
    public bool? EnableBlobFiles { get; set; }

    public ulong ReceiptsDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint ReceiptsDbWriteBufferNumber { get; set; } = 4;
    public ulong ReceiptsDbBlockCacheSize { get; set; } = (ulong)32.MiB();
    public bool ReceiptsDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? ReceiptsDbMaxOpenFiles { get; set; }
    public long? ReceiptsDbRateLimiterBytesPerSec { get; set; }
    public bool? ReceiptsDbUseDirectReads { get; set; }
    public bool? ReceiptsDbUseDirectIOForFlushAndCompaction { get; set; }
    public bool? ReceiptsDbEnableBlobFiles { get; set; }

    public ulong BlocksDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint BlocksDbWriteBufferNumber { get; set; } = 4;
    public ulong BlocksDbBlockCacheSize { get; set; } = (ulong)32.MiB();
    public bool BlocksDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? BlocksDbMaxOpenFiles { get; set; }
    public long? BlocksDbRateLimiterBytesPerSec { get; set; }
    public bool? BlocksDbUseDirectReads { get; set; }
    public bool? BlocksDbUseDirectIOForFlushAndCompaction { get; set; }
    public bool? BlocksDbEnableBlobFiles { get; set; }

    public ulong HeadersDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint HeadersDbWriteBufferNumber { get; set; } = 4;
    public ulong HeadersDbBlockCacheSize { get; set; } = (ulong)32.MiB();
    public bool HeadersDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? HeadersDbMaxOpenFiles { get; set; }
    public long? HeadersDbRateLimiterBytesPerSec { get; set; }
    public bool? HeadersDbUseDirectReads { get; set; }
    public bool? HeadersDbUseDirectIOForFlushAndCompaction { get; set; }
    public bool? HeadersDbEnableBlobFiles { get; set; }

    public ulong BlockInfosDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint BlockInfosDbWriteBufferNumber { get; set; } = 4;
    public ulong BlockInfosDbBlockCacheSize { get; set; } = (ulong)32.MiB();
    public bool BlockInfosDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? BlockInfosDbMaxOpenFiles { get; set; }
    public long? BlockInfosDbRateLimiterBytesPerSec { get; set; }
    public bool? BlockInfoUseDirectReads { get; set; }
    public bool? BlockInfoUseDirectIOForFlushAndCompaction { get; set; }
    public bool? BlockInfoEnableBlobFiles { get; set; }

    public ulong PendingTxsDbWriteBufferSize { get; set; } = (ulong)4.MiB();
    public uint PendingTxsDbWriteBufferNumber { get; set; } = 4;
    public ulong PendingTxsDbBlockCacheSize { get; set; } = (ulong)16.MiB();
    public bool PendingTxsDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? PendingTxsDbMaxOpenFiles { get; set; }
    public long? PendingTxsDbRateLimiterBytesPerSec { get; set; }
    public bool? PendingTxsUseDirectReads { get; set; }
    public bool? PendingTxsUseDirectIOForFlushAndCompaction { get; set; }
    public bool? PendingTxsEnableBlobFiles { get; set; }

    public ulong CodeDbWriteBufferSize { get; set; } = (ulong)2.MiB();
    public uint CodeDbWriteBufferNumber { get; set; } = 4;
    public ulong CodeDbBlockCacheSize { get; set; } = (ulong)8.MiB();
    public bool CodeDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? CodeDbMaxOpenFiles { get; set; }
    public long? CodeDbRateLimiterBytesPerSec { get; set; }
    public bool? CodeDbUseDirectReads { get; set; }
    public bool? CodeDbUseDirectIOForFlushAndCompaction { get; set; }
    public bool? CodeDbEnableBlobFiles { get; set; }

    public ulong BloomDbWriteBufferSize { get; set; } = (ulong)1.KiB();
    public uint BloomDbWriteBufferNumber { get; set; } = 4;
    public ulong BloomDbBlockCacheSize { get; set; } = (ulong)1.KiB();
    public bool BloomDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? BloomDbMaxOpenFiles { get; set; }
    public long? BloomDbRateLimiterBytesPerSec { get; set; }
    public bool? BloomDbUseDirectReads { get; set; }
    public bool? BloomDbUseDirectIOForFlushAndCompaction { get; set; }
    public bool? BloomDbEnableBlobFiles { get; set; }

    public ulong WitnessDbWriteBufferSize { get; set; } = (ulong)1.KiB();
    public uint WitnessDbWriteBufferNumber { get; set; } = 4;
    public ulong WitnessDbBlockCacheSize { get; set; } = (ulong)1.KiB();
    public bool WitnessDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? WitnessDbMaxOpenFiles { get; set; }
    public long? WitnessDbRateLimiterBytesPerSec { get; set; }
    public bool? WitnessDbUseDirectReads { get; set; }
    public bool? WitnessDbUseDirectIOForFlushAndCompaction { get; set; }
    public bool? WitnessDbEnableBlobFiles { get; set; }

    // TODO - profile and customize
    public ulong CanonicalHashTrieDbWriteBufferSize { get; set; } = (ulong)2.MB();
    public uint CanonicalHashTrieDbWriteBufferNumber { get; set; } = 4;
    public ulong CanonicalHashTrieDbBlockCacheSize { get; set; } = (ulong)8.MB();
    public bool CanonicalHashTrieDbCacheIndexAndFilterBlocks { get; set; } = true;
    public int? CanonicalHashTrieDbMaxOpenFiles { get; set; }
    public long? CanonicalHashTrieDbRateLimiterBytesPerSec { get; set; }
    public bool? CanonicalHashTrieDbUseDirectReads { get; set; }
    public bool? CanonicalHashTrieDbUseDirectIOForFlushAndCompaction { get; set; }
    public bool? CanonicalHashTrieDbEnableBlobFiles { get; set; }

    public ulong MetadataDbWriteBufferSize { get; set; } = (ulong)1.KiB();
    public uint MetadataDbWriteBufferNumber { get; set; } = 4;
    public ulong MetadataDbBlockCacheSize { get; set; } = (ulong)1.KiB();
    public bool MetadataDbCacheIndexAndFilterBlocks { get; set; } = true;
    public int? MetadataDbMaxOpenFiles { get; set; }
    public long? MetadataDbRateLimiterBytesPerSec { get; set; }
    public bool? MetadataDbUseDirectReads { get; set; }
    public bool? MetadataDbUseDirectIOForFlushAndCompaction { get; set; }
    public bool? MetadataDbEnableBlobFiles { get; set; }

    public uint RecycleLogFileNum { get; set; } = 0;
    public bool WriteAheadLogSync { get; set; } = false;
    public long? RateLimiterBytesPerSec { get; set; }
    public bool? UseDirectReads { get; set; }
    public bool? UseDirectIOForFlushAndCompaction { get; set; }

    public bool EnableDbStatistics { get; set; } = false;
    public bool EnableMetricsUpdater { get; set; } = false;
    public uint StatsDumpPeriodSec { get; set; } = 600;
}
