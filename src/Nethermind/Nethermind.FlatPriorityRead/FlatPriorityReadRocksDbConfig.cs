// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db.Rocks.Config;

namespace Nethermind.FlatPriorityRead;

/// <summary>The RocksDB configuration of <see cref="RocksDbPrioritySlotStore"/>, built for point reads from tmpfs.</summary>
/// <remarks>
/// <para>
/// PlainTable with a prefix extractor covering the whole 33-byte key gives every SST an in-memory hash index and a
/// per-key bloom filter, so a lookup is a hash probe into the memory-mapped file: no block index, no binary search
/// and no block cache. It is the setup of the RocksDB wiki's in-memory benchmark
/// (https://github.com/facebook/rocksdb/wiki/RocksDB-In-Memory-Workload-Performance-Benchmarks).
/// PlainTable needs mmap reads, cannot compress, and its files must stay below 2^31 bytes.
/// </para>
/// <para>
/// Dynamic level sizing with an L0 trigger of one keeps the tree to a few levels, per the in-memory profile of the
/// RocksDB tuning guide. The WAL, checksums and flushing on exit are not needed, since the database is rebuilt at
/// every start. The write buffer, target file size and level base sizes must stay in the string because
/// <see cref="Nethermind.Db.Rocks.RocksDbFactory"/> and <see cref="Nethermind.Db.Rocks.DbOnTheRocks"/> read them from it.
/// </para>
/// </remarks>
public sealed class FlatPriorityReadRocksDbConfig(IFlatPriorityReadConfig config, IDbConfig dbConfig) : IRocksDbConfig
{
    public const string Options =
        "write_buffer_size=67108864;" +
        "max_write_buffer_number=4;" +
        "min_write_buffer_number_to_merge=1;" +
        "memtable_whole_key_filtering=true;" +
        "memtable_prefix_bloom_size_ratio=0.1;" +
        "allow_mmap_reads=true;" +
        "compression=kNoCompression;" +
        "bottommost_compression=kNoCompression;" +
        "max_open_files=-1;" +
        "avoid_flush_during_shutdown=true;" +
        "level_compaction_dynamic_level_bytes=true;" +
        "level0_file_num_compaction_trigger=1;" +
        "target_file_size_base=268435456;" +
        "max_bytes_for_level_base=268435456;" +
        "prefix_extractor=fixed:33;" +
        "table_factory={id=PlainTable;user_key_len=33;bloom_bits_per_key=10;hash_table_ratio=0.75;index_sparseness=16;encoding_type=kPlain;full_scan_mode=false;store_index_in_file=false};";

    public ulong? WriteBufferSize => null;
    public ulong? WriteBufferNumber => null;
    public string RocksDbOptions => Options;
    public string AdditionalRocksDbOptions => config.RocksDbAdditionalOptions ?? "";
    public int? MaxOpenFiles => -1;
    public bool WriteAheadLogSync => false;
    public ulong? ReadAheadSize => 0;
    public bool EnableDbStatistics => dbConfig.EnableDbStatistics;
    public uint StatsDumpPeriodSec => dbConfig.StatsDumpPeriodSec;
    public bool? VerifyChecksum => false;
    public ulong? RowCacheSize => null;
    public bool EnableFileWarmer => false;
    public double CompressibilityHint => 1.0;
    public FlushOnExitMode FlushOnExit => FlushOnExitMode.None;
    public nint? BlockCache => null;
}
