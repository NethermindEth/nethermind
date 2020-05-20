Known Issues
************

There are several known issues with the current version of Nethermind.

 Leaking Socket Descriptors
   On Linux our networking library is not closing socket descriptors properly. This results in the number of open files for the process growing indefinitely. Limits for the number of open files per process are different for root and other users. For root the limits are usually very high and the socket descriptors would probably not cause much trouble. Many of the cloud operators are launching VMs with root user access by default. If Nethermind process is frequently killed by OS then you may need to change the configuration for the maximum number of open files.
 
 RocksDB on macOS
   RocksDB library does not always load properly on macOS. One (hacky) workaround is to install the latest version of RocksDB by running brew install rocksdb.

 Skipping consensus issues blocks
   We do our best in Nethermind not to have consensus issues with other clients. But historically consensus issues had happened. In that case we start working on a hotfix immediately and release it within hours time. If you need your node to be operational ASAP and can't wait for hotfix you do have an option to achieve that. Nethermind node allows you to fast sync to recent blocks and state. When node does fast sync it can skip over processing problematic blocks. In order to be able to fast sync we need SyncConfig.FastSync to be set to 'true'. You also need to set SyncConfig.FastSyncCatchUpHeightDelta to a value lower than how far your node is behind the chain. SyncConfig.FastSyncCatchUpHeightDelta is the minimum difference between current chain height and chain head block number when node can switch from full sync (block processing) to fast sync. By default it is set to 1024. Please note that we don't recommend setting this value to less than 32 in normal circumstances. After setting those values and restarting node, the node will download block headers, bodies (if SyncConfig.DownloadBodiesInFastSync is 'true'), receipts (if SyncConfig.DownloadReceiptsInFastSync is 'true') and current state. After that it will resume processing from new head block. Please note that the historical state for skipped blocks might not be available. This can cause some JSON RPC calls on the historical state not to work - same situation as if these blocks state was pruned.
   
   For example if current chain head block number is 10,000,100 and node couldn't process block 10,000,000 due to consensus issue, if you set FastSync:true and FastSyncCatchUpHeightDelta:100 (or as low as 32) and node should switch to fast sync, catch up with current chain head and switch back to full sync.
   
   The time that it will take to fast sync to current chain head can take even up to 2 hours depending how many blocks and how much new state there is to be downloaded.
