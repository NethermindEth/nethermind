Build
*****

IDE
^^^

* JetBrains Rider https://www.jetbrains.com/rider/
* VS Code https://code.visualstudio.com/docs/other/dotnet

SDKs
^^^^

* Windows https://www.microsoft.com/net/download?initial-os=windows
* Linux https://www.microsoft.com/net/download?initial-os=linux (make sure to select the right distribution)
* Mac https://www.microsoft.com/net/download?initial-os=macos

Linux
^^^^^

::

    sudo apt-get update && sudo apt-get install libsnappy-dev libc6-dev libc6
    git clone https://github.com/tkstanczak/nethermind --recursive
    cd nethermind/src/Nethermind
    dotnet build -c Release
    cd Nethermind.Runner
    dotnet run

MacOS
^^^^^

::

    brew install gmp
    brew install snappy
    git clone https://github.com/tkstanczak/nethermind --recursive
    cd nethermind/src/Nethermind
    dotnet build -c Release
    cd Nethermind.Runner
    dotnet run
    
Windows
^^^^^^^

you may need to install https://support.microsoft.com/en-us/help/2977003/the-latest-supported-visual-c-downloads

::

    git clone https://github.com/tkstanczak/nethermind --recursive
    cd nethermind/src/Nethermind
    dotnet build -c Release
    cd Nethermind.Runner
    dotnet run
