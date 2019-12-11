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

    docker run -it -p 30303:30303 -p 30303:30303/udp nethermind/nethermind:alpine

Port 30303 has to be accessible if you want incoming connections. (tcp and udp)

Enable JSON-RPC
---------------
::

    docker run -it -p 127.0.0.1:8545:8545 -p 30303:30303 -p 30303:30303/udp nethermind/nethermind:alpine --JsonRpc.Enabled true --JsonRpc.Host 0.0.0.0

Be carefull to use ``-p 127.0.0.1:8545:8545`` and NOT ``-p 8545:8545``!


Persistent data
---------------

The database is located at ``/data``
::

    docker volume create nethermind_db
    docker run -it -p 30303:30303 -p 30303:30303/udp -v nethermind_db:/data nethermind/nethermind:alpine

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
