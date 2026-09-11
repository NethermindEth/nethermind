# FlatDB migration prototype

This prototype runs Nethermind with FlatDB as the state backend. The Hash and HalfPath state schemas are deprecated in the assumed Nethermind 2.1 compatibility release and are rejected by this prototype before it opens or mutates the legacy database.

The compatibility plan assumes a future Nethermind 2.1 release will remain available to open the old database and migrate it to FlatDB. Run the supported old binary's importer, then start this version on the resulting FlatDB. The importer flag remains a configuration tombstone so an old configuration fails with a useful migration message.

If the old database cannot be migrated in place, use a fresh database directory and resync with FlatDB. Keep the old database available until the new node has been checked; this prototype does not make a claim about preserving arbitrary historical state files.

Configuration details are documented in the [FlatDB import guide](https://docs.nethermind.io/next/fundamentals/configuration/#flatdbimportfrompruningtriestate).
