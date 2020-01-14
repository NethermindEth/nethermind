Docker (arm32)
***************

Using an existing image
=======================

Docker images are available for both arm64 and arm32 architectures.

https://hub.docker.com/r/nethermind/nethermind/

Docker pull command::
    
    docker pull nethermind/nethermind:arm32

You can use ``--help`` to get a list of possible start parameters for Nethermind::

    docker run -it nethermind/nethermind:arm32 --help

Sync with mainnet
-----------------
::

    docker run -it -p 30303:30303 -p 30303:30303/udp nethermind/nethermind:arm32

Port 30303 has to be accessible if you want incoming connections. (tcp and udp)

Enable JSON-RPC
---------------
::

    docker run -it -p 127.0.0.1:8545:8545 -p 30303:30303 -p 30303:30303/udp nethermind/nethermind:arm32 --JsonRpc.Enabled true --JsonRpc.Host 0.0.0.0

Be carefull to use ``-p 127.0.0.1:8545:8545`` and NOT ``-p 8545:8545``!


Build the image
===============
::

    git clone --recursive https://github.com/NethermindEth/nethermind
    docker build nethermind -f nethermind/Dockerfile_arm32 -t nethermind
    docker run -it nethermind --help