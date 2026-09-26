# FlatDB migration prototype

This prototype always uses FlatDB for world state. The Hash and HalfPath state schemas are deprecated in the assumed
Nethermind 2.1 compatibility release and are rejected before startup opens the state database. Init.StateDbKeyScheme
may be empty or Current (case insensitive); any other value is rejected.

Before using this prototype, migrate with a compatibility binary that still supports the importer, then start this
version on the resulting FlatDB. This prototype does not migrate historical state automatically and makes no promise
to preserve arbitrary files from the old state database. If an in-place migration is not possible, use a fresh database
directory and resync with FlatDB. Keep the old database until the new node has been checked.

FlatDb.Enabled=false is retained as a configuration tombstone and causes startup to fail with a migration message.
FlatDb.ImportFromPruningTrieState was removed: this version cannot import a Hash or HalfPath database, so run the import
with the compatibility binary (--FlatDb.ImportFromPruningTrieState=true) before upgrading. A fresh FlatDB database also
refuses to start when legacy state files remain under the configured state path. A populated FlatDB takes precedence,
so the old state directory can be removed after a successful migration and verification.

The old pruning controls were removed because FlatDB has no legacy trie pruning path. The following Pruning.* keys are
no longer supported:

- Pruning.Enabled
- Pruning.Mode
- Pruning.CacheMb
- Pruning.DirtyCacheMb
- Pruning.PersistenceInterval
- Pruning.FullPruningThresholdMb
- Pruning.FullPruningTrigger
- Pruning.FullPruningMaxDegreeOfParallelism
- Pruning.FullPruningMemoryBudgetMb
- Pruning.FullPruningDisableLowPriorityWrites
- Pruning.FullPruningMinimumDelayHours
- Pruning.FullPruningCompletionBehavior
- Pruning.AvailableSpaceCheckEnabled
- Pruning.TrackedPastKeyCountMemoryRatio
- Pruning.TrackPastKeys
- Pruning.DirtyNodeShardBit
- Pruning.PrunePersistedNodePortion
- Pruning.PrunePersistedNodeMinimumTarget
- Pruning.MaxUnpersistedBlockCount
- Pruning.MinUnpersistedBlockCount
- Pruning.MaxBufferedCommitCount
- Pruning.SimulateLongFinalizationDepth
- Pruning.PruneDelayMilliseconds

Pruning.PruningBoundary remains valid, but only as the fallback for LogIndex.MaxReorgDepth; it no longer bounds how
much state is kept (FlatDb.MinReorgDepth and FlatDb.MaxReorgDepth do). The old Db.StateDb* RocksDB tuning keys were
also removed:

- Db.StateDbWriteBufferSize
- Db.StateDbWriteBufferNumber
- Db.StateDbVerifyChecksum
- Db.StateDbRowCacheSize
- Db.StateDbEnableFileWarmer
- Db.StateDbCompressibilityHint
- Db.StateDbRocksDbOptions
- Db.StateDbAdditionalRocksDbOptions
- Db.StateDbLargeMemoryRocksDbOptions
- Db.StateDbArchiveModeRocksDbOptions
- Db.StateDbLargeMemoryWriteBufferSize
- Db.StateDbArchiveModeWriteBufferSize

These keys were removed as well:

- FlatDb.ImportFromPruningTrieState
- FlatDb.DropPruningTrieState
- Sync.TrieHealing

The admin_prune JSON-RPC method was removed. Calls now return JSON-RPC method not found, and FlatDB has no full-pruning
replacement. Configure the FlatDB history features explicitly when historical reads are required. For an old archive
configuration, map Pruning.Mode=None to FlatDb.HistoryEnabled=true.

The legacy `state` and `storage` database mappings were removed from `debug_getFromDb` because those databases no longer
exist. Callers must stop using those database names; an unknown database name currently returns a JSON-RPC error.

Remove deleted settings from JSON configuration files, NETHERMIND_* environment variables and command lines during
migration. Unknown keys in JSON files and environment variables are ignored by configuration binding and reported as
Invalid configuration settings warnings; those warnings do not restore the old pruning behavior. A removed key passed on
the command line (for example --Pruning.Mode=None) fails argument parsing, so the node does not start until it is
removed from the command line, container arguments or scripts.

These metrics were only produced by the removed backends and are no longer exported:

- nethermind_state_db_pruning, nethermind_state_db_in_pruning_writes, nethermind_full_pruning_count and
  nethermind_full_pruning_last_duration_seconds
- nethermind_importer_entries_count and nethermind_importer_entries_count_flat
- the trie store cache gauges and counters: nethermind_dirty_nodes_count, nethermind_cached_nodes_count,
  nethermind_persisted_node_count, nethermind_removed_node_count, nethermind_committed_nodes_count,
  nethermind_pruned_persisted_nodes_count, nethermind_deep_pruned_persisted_nodes_count,
  nethermind_pruned_transient_nodes_count, nethermind_loaded_from_rlp_cache_nodes_count, nethermind_replaced_nodes_count,
  nethermind_snapshot_persistence_time_ms, nethermind_pruning_time_ms, nethermind_persisted_node_pruning_time_ms,
  nethermind_deep_pruning_time_ms, nethermind_last_persisted_block_number and nethermind_dirty_memory_used_by_cache
- nethermind_state_reader_reads and nethermind_storage_reader_reads

The PruningMode tag that every metric carries no longer reports the Pruning.Mode value. It is `FlatArchive` when
FlatDb.HistoryEnabled is true and `Flat` otherwise.

Configuration details are documented in the [FlatDB import guide](https://docs.nethermind.io/next/fundamentals/configuration/#flatdbimportfrompruningtriestate).
