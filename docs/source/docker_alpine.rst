Docker (Alpine)
***************

Using an existing image
=======================

https://hub.docker.com/r/nethermind/nethermind/

You can use ``--help`` to get a list of possible start parameters for Nethermind::

    docker run -it nethermind/nethermind:alpine --help

Sync with mainnet
-----------------
::

    docker run -it --network host nethermind/nethermind:alpine

Port 30303 has to be accessible if you want incoming connections. (tcp and udp)

Enable JSON-RPC
---------------
::

    docker run -it --network host nethermind/nethermind:alpine --JsonRpc.Enabled true
    
The default port is 8545.

Persistent data
---------------

The database is located at ``/data``
::

    docker volume create nethermind_db
    docker run -it --network host -v nethermind_db:/data nethermind/nethermind:alpine

To use an existing database, you need to make the files accessible for the user inside the container. (uid/gid 1337)

Override configuration file
---------------------------
::

    -v /absolute/path/mainnet.cfg:/nethermind/configs/mainnet.cfg

The file has to be accessible for the user inside the container. (uid/gid 1337)

Build the image
===============
::

    git clone --recursive https://github.com/NethermindEth/nethermind
    docker build nethermind -f nethermind/Dockerfile_alpine -t nethermind
    docker run -it nethermind --help
