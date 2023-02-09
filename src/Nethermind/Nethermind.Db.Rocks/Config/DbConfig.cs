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

    public ulong ReceiptsDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint ReceiptsDbWriteBufferNumber { get; set; } = 4;
    public ulong ReceiptsDbBlockCacheSize { get; set; } = (ulong)32.MiB();
    public bool ReceiptsDbCacheIndexAndFilterBlocks { get; set; } = false;

    public ulong BlocksDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint BlocksDbWriteBufferNumber { get; set; } = 4;
    public ulong BlocksDbBlockCacheSize { get; set; } = (ulong)32.MiB();
    public bool BlocksDbCacheIndexAndFilterBlocks { get; set; } = false;

    public ulong HeadersDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint HeadersDbWriteBufferNumber { get; set; } = 4;
    public ulong HeadersDbBlockCacheSize { get; set; } = (ulong)32.MiB();
    public bool HeadersDbCacheIndexAndFilterBlocks { get; set; } = false;

    public ulong BlockInfosDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint BlockInfosDbWriteBufferNumber { get; set; } = 4;
    public ulong BlockInfosDbBlockCacheSize { get; set; } = (ulong)32.MiB();
    public bool BlockInfosDbCacheIndexAndFilterBlocks { get; set; } = false;

    public ulong PendingTxsDbWriteBufferSize { get; set; } = (ulong)4.MiB();
    public uint PendingTxsDbWriteBufferNumber { get; set; } = 4;
    public ulong PendingTxsDbBlockCacheSize { get; set; } = (ulong)16.MiB();
    public bool PendingTxsDbCacheIndexAndFilterBlocks { get; set; } = false;

    public ulong CodeDbWriteBufferSize { get; set; } = (ulong)2.MiB();
    public uint CodeDbWriteBufferNumber { get; set; } = 4;
    public ulong CodeDbBlockCacheSize { get; set; } = (ulong)8.MiB();
    public bool CodeDbCacheIndexAndFilterBlocks { get; set; } = false;

    public ulong BloomDbWriteBufferSize { get; set; } = (ulong)1.KiB();
    public uint BloomDbWriteBufferNumber { get; set; } = 4;
    public ulong BloomDbBlockCacheSize { get; set; } = (ulong)1.KiB();
    public bool BloomDbCacheIndexAndFilterBlocks { get; set; } = false;

    public ulong WitnessDbWriteBufferSize { get; set; } = (ulong)1.KiB();
    public uint WitnessDbWriteBufferNumber { get; set; } = 4;
    public ulong WitnessDbBlockCacheSize { get; set; } = (ulong)1.KiB();
    public bool WitnessDbCacheIndexAndFilterBlocks { get; set; } = false;

    // TODO - profile and customize
    public ulong CanonicalHashTrieDbWriteBufferSize { get; set; } = (ulong)2.MB();
    public uint CanonicalHashTrieDbWriteBufferNumber { get; set; } = 4;
    public ulong CanonicalHashTrieDbBlockCacheSize { get; set; } = (ulong)8.MB();
    public bool CanonicalHashTrieDbCacheIndexAndFilterBlocks { get; set; } = true;

    public ulong MetadataDbWriteBufferSize { get; set; } = (ulong)1.KiB();
    public uint MetadataDbWriteBufferNumber { get; set; } = 4;
    public ulong MetadataDbBlockCacheSize { get; set; } = (ulong)1.KiB();
    public bool MetadataDbCacheIndexAndFilterBlocks { get; set; } = true;

    public uint RecycleLogFileNum { get; set; } = 0;
    public bool WriteAheadLogSync { get; set; } = false;

    public bool EnableDbStatistics { get; set; } = false;
    public bool EnableMetricsUpdater { get; set; } = false;
    public uint StatsDumpPeriodSec { get; set; } = 600;
}
