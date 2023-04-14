// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
    public long? MaxWriteBytesPerSec { get; set; }
    public IDictionary<string, string>? AdditionalRocksDbOptions { get; set; }

    public ulong ReceiptsDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint ReceiptsDbWriteBufferNumber { get; set; } = 4;
    public ulong ReceiptsDbBlockCacheSize { get; set; } = (ulong)32.MiB();
    public bool ReceiptsDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? ReceiptsDbMaxOpenFiles { get; set; }
    public long? ReceiptsDbMaxWriteBytesPerSec { get; set; }
    public IDictionary<string, string>? ReceiptsDbAdditionalRocksDbOptions { get; set; }

    public ulong BlocksDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint BlocksDbWriteBufferNumber { get; set; } = 4;
    public ulong BlocksDbBlockCacheSize { get; set; } = (ulong)32.MiB();
    public bool BlocksDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? BlocksDbMaxOpenFiles { get; set; }
    public long? BlocksDbMaxWriteBytesPerSec { get; set; }
    public IDictionary<string, string>? BlocksDbAdditionalRocksDbOptions { get; set; }

    public ulong HeadersDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint HeadersDbWriteBufferNumber { get; set; } = 4;
    public ulong HeadersDbBlockCacheSize { get; set; } = (ulong)32.MiB();
    public bool HeadersDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? HeadersDbMaxOpenFiles { get; set; }
    public long? HeadersDbMaxWriteBytesPerSec { get; set; }
    public IDictionary<string, string>? HeadersDbAdditionalRocksDbOptions { get; set; }

    public ulong BlockInfosDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint BlockInfosDbWriteBufferNumber { get; set; } = 4;
    public ulong BlockInfosDbBlockCacheSize { get; set; } = (ulong)32.MiB();
    public bool BlockInfosDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? BlockInfosDbMaxOpenFiles { get; set; }
    public long? BlockInfosDbMaxWriteBytesPerSec { get; set; }
    public IDictionary<string, string>? BlockInfosDbAdditionalRocksDbOptions { get; set; }

    public ulong PendingTxsDbWriteBufferSize { get; set; } = (ulong)4.MiB();
    public uint PendingTxsDbWriteBufferNumber { get; set; } = 4;
    public ulong PendingTxsDbBlockCacheSize { get; set; } = (ulong)16.MiB();
    public bool PendingTxsDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? PendingTxsDbMaxOpenFiles { get; set; }
    public long? PendingTxsDbMaxWriteBytesPerSec { get; set; }
    public IDictionary<string, string>? PendingTxsDbAdditionalRocksDbOptions { get; set; }

    public ulong CodeDbWriteBufferSize { get; set; } = (ulong)2.MiB();
    public uint CodeDbWriteBufferNumber { get; set; } = 4;
    public ulong CodeDbBlockCacheSize { get; set; } = (ulong)8.MiB();
    public bool CodeDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? CodeDbMaxOpenFiles { get; set; }
    public long? CodeDbMaxWriteBytesPerSec { get; set; }
    public IDictionary<string, string>? CodeDbAdditionalRocksDbOptions { get; set; }

    public ulong BloomDbWriteBufferSize { get; set; } = (ulong)1.KiB();
    public uint BloomDbWriteBufferNumber { get; set; } = 4;
    public ulong BloomDbBlockCacheSize { get; set; } = (ulong)1.KiB();
    public bool BloomDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? BloomDbMaxOpenFiles { get; set; }
    public long? BloomDbMaxWriteBytesPerSec { get; set; }
    public IDictionary<string, string>? BloomDbAdditionalRocksDbOptions { get; set; }

    public ulong WitnessDbWriteBufferSize { get; set; } = (ulong)1.KiB();
    public uint WitnessDbWriteBufferNumber { get; set; } = 4;
    public ulong WitnessDbBlockCacheSize { get; set; } = (ulong)1.KiB();
    public bool WitnessDbCacheIndexAndFilterBlocks { get; set; } = false;
    public int? WitnessDbMaxOpenFiles { get; set; }
    public long? WitnessDbMaxWriteBytesPerSec { get; set; }
    public IDictionary<string, string>? WitnessDbAdditionalRocksDbOptions { get; set; }

    // TODO - profile and customize
    public ulong CanonicalHashTrieDbWriteBufferSize { get; set; } = (ulong)2.MB();
    public uint CanonicalHashTrieDbWriteBufferNumber { get; set; } = 4;
    public ulong CanonicalHashTrieDbBlockCacheSize { get; set; } = (ulong)8.MB();
    public bool CanonicalHashTrieDbCacheIndexAndFilterBlocks { get; set; } = true;
    public int? CanonicalHashTrieDbMaxOpenFiles { get; set; }
    public long? CanonicalHashTrieDbMaxWriteBytesPerSec { get; set; }
    public IDictionary<string, string>? CanonicalHashTrieDbAdditionalRocksDbOptions { get; set; }

    public ulong MetadataDbWriteBufferSize { get; set; } = (ulong)1.KiB();
    public uint MetadataDbWriteBufferNumber { get; set; } = 4;
    public ulong MetadataDbBlockCacheSize { get; set; } = (ulong)1.KiB();
    public bool MetadataDbCacheIndexAndFilterBlocks { get; set; } = true;
    public int? MetadataDbMaxOpenFiles { get; set; }
    public long? MetadataDbMaxWriteBytesPerSec { get; set; }
    public IDictionary<string, string>? MetadataDbAdditionalRocksDbOptions { get; set; }

    public uint RecycleLogFileNum { get; set; } = 0;
    public bool WriteAheadLogSync { get; set; } = false;

    public bool EnableDbStatistics { get; set; } = false;
    public bool EnableMetricsUpdater { get; set; } = false;
    public uint StatsDumpPeriodSec { get; set; } = 600;
}
