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
    public ulong? ReadAheadSize { get; set; } = (ulong)256.KiB();
    public bool? UseDirectReads { get; set; } = false;
    public bool? UseDirectIoForFlushAndCompactions { get; set; } = false;
    public bool? DisableCompression { get; set; } = false;
    public ulong? CompactionReadAhead { get; set; }
    public IDictionary<string, string>? AdditionalRocksDbOptions { get; set; }
    public ulong? MaxBytesForLevelBase { get; set; } = (ulong)256.MiB();
    public ulong TargetFileSizeBase { get; set; } = (ulong)64.MiB();
    public int TargetFileSizeMultiplier { get; set; } = 1;

    public ulong ReceiptsDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint ReceiptsDbWriteBufferNumber { get; set; } = 4;
    public ulong ReceiptsDbBlockCacheSize { get; set; } = 0;
    public bool ReceiptsDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? ReceiptsDbMaxOpenFiles { get; set; }
    public long? ReceiptsDbMaxBytesPerSec { get; set; }
    public int? ReceiptsDbBlockSize { get; set; }
    public bool? ReceiptsDbUseDirectReads { get; set; }
    public bool? ReceiptsDbUseDirectIoForFlushAndCompactions { get; set; }
    public ulong? ReceiptsDbCompactionReadAhead { get; set; }
    public ulong ReceiptsDbTargetFileSizeBase { get; set; } = (ulong)256.MiB();
    public IDictionary<string, string>? ReceiptsDbAdditionalRocksDbOptions { get; set; }

    public ulong BlocksDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint BlocksDbWriteBufferNumber { get; set; } = 4;
    public ulong BlocksDbBlockCacheSize { get; set; } = 0;
    public bool BlocksDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? BlocksDbMaxOpenFiles { get; set; }
    public long? BlocksDbMaxBytesPerSec { get; set; }
    public int? BlocksBlockSize { get; set; }
    public bool? BlocksDbUseDirectReads { get; set; }
    public bool? BlocksDbUseDirectIoForFlushAndCompactions { get; set; }
    public ulong? BlocksDbCompactionReadAhead { get; set; }
    public IDictionary<string, string>? BlocksDbAdditionalRocksDbOptions { get; set; }

    public ulong HeadersDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint HeadersDbWriteBufferNumber { get; set; } = 4;
    public ulong HeadersDbBlockCacheSize { get; set; } = 0;
    public bool HeadersDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? HeadersDbMaxOpenFiles { get; set; }
    public long? HeadersDbMaxBytesPerSec { get; set; }
    public int? HeadersDbBlockSize { get; set; }
    public bool? HeadersDbUseDirectReads { get; set; }
    public bool? HeadersDbUseDirectIoForFlushAndCompactions { get; set; }
    public ulong? HeadersDbCompactionReadAhead { get; set; }
    public IDictionary<string, string>? HeadersDbAdditionalRocksDbOptions { get; set; }
    public ulong? HeadersDbMaxBytesForLevelBase { get; set; } = (ulong)128.MiB();

    public ulong BlockInfosDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint BlockInfosDbWriteBufferNumber { get; set; } = 4;
    public ulong BlockInfosDbBlockCacheSize { get; set; } = 0;
    public bool BlockInfosDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? BlockInfosDbMaxOpenFiles { get; set; }
    public long? BlockInfosDbMaxBytesPerSec { get; set; }
    public int? BlockInfosDbBlockSize { get; set; }
    public bool? BlockInfosDbUseDirectReads { get; set; }
    public bool? BlockInfosDbUseDirectIoForFlushAndCompactions { get; set; }
    public ulong? BlockInfosDbCompactionReadAhead { get; set; }
    public IDictionary<string, string>? BlockInfosDbAdditionalRocksDbOptions { get; set; }

    public ulong PendingTxsDbWriteBufferSize { get; set; } = (ulong)4.MiB();
    public uint PendingTxsDbWriteBufferNumber { get; set; } = 4;
    public ulong PendingTxsDbBlockCacheSize { get; set; } = 0;
    public bool PendingTxsDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? PendingTxsDbMaxOpenFiles { get; set; }
    public long? PendingTxsDbMaxBytesPerSec { get; set; }
    public int? PendingTxsDbBlockSize { get; set; }
    public bool? PendingTxsDbUseDirectReads { get; set; }
    public bool? PendingTxsDbUseDirectIoForFlushAndCompactions { get; set; }
    public ulong? PendingTxsDbCompactionReadAhead { get; set; }
    public IDictionary<string, string>? PendingTxsDbAdditionalRocksDbOptions { get; set; }

    public ulong CodeDbWriteBufferSize { get; set; } = (ulong)2.MiB();
    public uint CodeDbWriteBufferNumber { get; set; } = 4;
    public ulong CodeDbBlockCacheSize { get; set; } = 0;
    public bool CodeDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? CodeDbMaxOpenFiles { get; set; }
    public long? CodeDbMaxBytesPerSec { get; set; }
    public int? CodeDbBlockSize { get; set; }
    public bool? CodeUseDirectReads { get; set; } = false;
    public bool? CodeUseDirectIoForFlushAndCompactions { get; set; } = false;
    public ulong? CodeCompactionReadAhead { get; set; }
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
    public int? WitnessDbBlockSize { get; set; }
    public bool? WitnessUseDirectReads { get; set; } = false;
    public bool? WitnessUseDirectIoForFlushAndCompactions { get; set; } = false;
    public ulong? WitnessCompactionReadAhead { get; set; }
    public IDictionary<string, string>? WitnessDbAdditionalRocksDbOptions { get; set; }

    // TODO - profile and customize
    public ulong CanonicalHashTrieDbWriteBufferSize { get; set; } = (ulong)2.MB();
    public uint CanonicalHashTrieDbWriteBufferNumber { get; set; } = 4;
    public ulong CanonicalHashTrieDbBlockCacheSize { get; set; } = 0;
    public bool CanonicalHashTrieDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? CanonicalHashTrieDbMaxOpenFiles { get; set; }
    public long? CanonicalHashTrieDbMaxBytesPerSec { get; set; }
    public int? CanonicalHashTrieDbBlockSize { get; set; }
    public bool? CanonicalHashTrieUseDirectReads { get; set; } = false;
    public bool? CanonicalHashTrieUseDirectIoForFlushAndCompactions { get; set; } = false;
    public ulong? CanonicalHashTrieCompactionReadAhead { get; set; }
    public IDictionary<string, string>? CanonicalHashTrieDbAdditionalRocksDbOptions { get; set; }

    public ulong MetadataDbWriteBufferSize { get; set; } = (ulong)1.KiB();
    public uint MetadataDbWriteBufferNumber { get; set; } = 4;
    public ulong MetadataDbBlockCacheSize { get; set; } = 0;
    public bool MetadataDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? MetadataDbMaxOpenFiles { get; set; }
    public long? MetadataDbMaxBytesPerSec { get; set; }
    public int? MetadataDbBlockSize { get; set; }
    public bool? MetadataUseDirectReads { get; set; } = false;
    public bool? MetadataUseDirectIoForFlushAndCompactions { get; set; } = false;
    public ulong? MetadataCompactionReadAhead { get; set; }
    public IDictionary<string, string>? MetadataDbAdditionalRocksDbOptions { get; set; }

    public ulong StateDbWriteBufferSize { get; set; }
    public uint StateDbWriteBufferNumber { get; set; }
    public ulong StateDbBlockCacheSize { get; set; }
    public bool StateDbCacheIndexAndFilterBlocks { get; set; }
    public int? StateDbMaxOpenFiles { get; set; }
    public long? StateDbMaxBytesPerSec { get; set; }
    public int? StateDbBlockSize { get; set; } = 4 * 1024;
    public bool? StateDbUseDirectReads { get; set; } = false;
    public bool? StateDbUseDirectIoForFlushAndCompactions { get; set; } = false;
    public ulong? StateDbCompactionReadAhead { get; set; }
    public bool? StateDbDisableCompression { get; set; } = false;
    public int StateDbTargetFileSizeMultiplier { get; set; } = 2;
    public IDictionary<string, string>? StateDbAdditionalRocksDbOptions { get; set; }

    public uint RecycleLogFileNum { get; set; } = 0;
    public bool WriteAheadLogSync { get; set; } = false;

    public bool EnableDbStatistics { get; set; } = false;
    public bool EnableMetricsUpdater { get; set; } = false;
    public uint StatsDumpPeriodSec { get; set; } = 600;
}
