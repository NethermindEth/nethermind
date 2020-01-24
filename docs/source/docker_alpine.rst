Docker (Alpine)
***************

Prerequisites
=============

Install instructions for the Docker Engine can be found at: https://docs.docker.com/install

There are images for amd64 and arm64 processors. A list of all versions/tags is available at: https://hub.docker.com/r/nethermind/nethermind/tags

Port 30303 has to be accessible if you want incoming connections. (tcp and udp)

Persistent data / configuration
-------------------------------

The database and all configuration files are located at ``/data`` inside the docker container.
You need to mount this folder to your host system for persistence and easy accessibility.
The default configuration files will be copied into the ``/data`` folder after the first start.

You can use ``--help`` for this or to get a list of possible start parameters for Nethermind:
::

    docker run -it --volume /var/lib/nethermind/mainnet:/data nethermind/nethermind:alpine --help

The path ``/var/lib/nethermind/mainnet`` is just a recommendation and can be changed.

To use an existing database or configuration, you need to make the files accessible for the user inside the container. (uid/gid 1337)

Examples
========

Sync with mainnet
-----------------
::

    docker run -it --network host --volume /var/lib/nethermind/mainnet:/data nethermind/nethermind:alpine

Enable JSON-RPC
---------------
::

    docker run -it --network host --volume /var/lib/nethermind/mainnet:/data nethermind/nethermind:alpine --JsonRpc.Enabled true
    
The default JSON-RPC port is 8545.

Sync another network
--------------------
::

    docker run -it --network host --volume /var/lib/nethermind/goerli:/data nethermind/nethermind:alpine --config goerli

Run as daemon and start on (re)boot
---------------------------------

You can give your container a name, run it in the background and enable automatic restarts.

::

    docker run -d --name nethermind --restart always --network host --volume /var/lib/nethermind/mainnet:/data --stop-timeout 30 nethermind/nethermind:alpine
	
It is recommended to give Nethermind more time to exit gracefully with ``--stop-timeout 30`` in the case of a system shutdown or reboot.
	
If you want to see the current progress just use:

::

    docker logs nethermind -f

Updating the image
------------------

If you want to update your image, just delete the container and create a new one with the same parameters.

::

    docker stop nethermind --time 30
    docker container rm nethermind
    docker pull nethermind/nethermind:alpine
    docker run -d --name nethermind --restart always --network host --volume /var/lib/nethermind/mainnet:/data --stop-timeout 30 nethermind/nethermind:alpine

Advanced docker usage
---------------------

More information about docker is available at: https://docs.docker.com

Build the image yourself
========================
::

    git clone https://github.com/NethermindEth/nethermind
    docker build nethermind -f nethermind/Dockerfile_alpine -t nethermind
    docker run -it nethermind --help
