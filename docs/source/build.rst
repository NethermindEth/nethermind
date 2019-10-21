Build
*****

IDE (optional)
^^^^^^^^^^^^^^

* JetBrains Rider https://www.jetbrains.com/rider/
* VS Code https://code.visualstudio.com/docs/other/dotnet

SDKs
^^^^

You will need .NET SDK 3.0
 * Windows https://www.microsoft.com/net/download?initial-os=windows
 * Linux https://www.microsoft.com/net/download?initial-os=linux (make sure to select the right distribution)
 * Mac https://www.microsoft.com/net/download?initial-os=macos

Linux
^^^^^

::

    sudo apt-get update && sudo apt-get install libsnappy-dev libc6-dev libc6

MacOS
^^^^^

::

    brew install gmp && brew install snappy && brew install lz4
    
Windows
^^^^^^^

you may need to install https://support.microsoft.com/en-us/help/2977003/the-latest-supported-visual-c-downloads

All Platforms
^^^^^^^^^^^^^

::

    git clone https://github.com/tkstanczak/nethermind --recursive
    cd nethermind/src/Nethermind
    dotnet build Nethermind.sln -c Release

launch goerli testnet for quick sync and testing

::

    cd Nethermind.Runner
    dotnet run --no-build -c Release -- --config goerli   

You can confirm the latest block of the Goerli tetsnet here:
https://blockscout.com/eth/goerli/

or (mainnet)

::

    cd Nethermind.Runner
    dotnet run --no-build -c Release -- --config mainnet

Potential Issues
^^^^^^^^^^^^^^^^

If you have some previous pre-release versions of .NET Core installed they may cause conflicts. Your case might be quite unique so best to search for help online.

If application crashes saying that rocksdb-sharp / rocksdb is failing then most likely your processor is not supporting AVX instructions. This would require you to build RocksDb on your machine. We will add a tutorial for this later - for now reach out to us on gitter if you need to run it on a non-AVX CPU.
