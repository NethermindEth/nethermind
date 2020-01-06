Known Issues
************

There are several known issues with the current version of Nethermind.

 Leaking Socket Descriptors
   On Linux our networking library is not closing socket descriptors properly. This results in the number of open files for the process growing indefinitely. Limits for the number of open files per process are different for root and other users. For root the limits are usually very high and the socket descriptors would probably not cause much trouble. Many of the cloud operators are launching VMs with root user access by default. If Nethermind process is frequently killed by OS then you may need to change the configuration for the maximum number of open files.
 
 RocksDB on macOS
   RocksDB library does not always load properly on macOS. One (hacky) workaround is to install the latest version of RocksDB by running brew install rocksdb.
