# FlatDB migration prototype

This prototype always uses FlatDB for world state. The Hash and HalfPath state schemas are deprecated in the assumed
Nethermind 2.1 compatibility release and are rejected before startup opens the state database. Init.StateDbKeyScheme
may be empty or Current (case insensitive); any other value is rejected.

Before using this prototype, migrate with a compatibility binary that still supports the importer, then start this
version on the resulting FlatDB. This prototype does not migrate historical state automatically and makes no promise
to preserve arbitrary files from the old state database. If an in-place migration is not possible, use a fresh database
directory and resync with FlatDB. Keep the old database until the new node has been checked.

FlatDb.Enabled=false and FlatDb.ImportFromPruningTrieState=true are retained as configuration tombstones and cause
startup to fail with a migration message. A fresh FlatDB database also refuses to start when legacy state files remain
under the configured state path. A populated FlatDB takes precedence, so the old state directory can be removed after a
successful migration and verification.

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

Pruning.PruningBoundary remains valid as the fallback reorganization depth for the block tree and log index. The old
Db.StateDb* RocksDB tuning keys were also removed:

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

The admin_prune JSON-RPC method was removed. Calls now return JSON-RPC method not found, and FlatDB has no full-pruning
replacement. Configure the FlatDB history features explicitly when historical reads are required. For an old archive
configuration, map Pruning.Mode=None to FlatDb.HistoryEnabled=true.

Remove deleted settings from JSON configuration files and NETHERMIND_* environment variables during migration. Unknown
keys are ignored by configuration binding and reported as Invalid configuration settings warnings; those warnings do not
restore the old pruning behavior. Check startup output and clean the old keys rather than relying on the warnings.

Configuration details are documented in the [FlatDB import guide](https://docs.nethermind.io/next/fundamentals/configuration/#flatdbimportfrompruningtriestate).
