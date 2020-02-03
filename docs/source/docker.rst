Docker (Ubuntu/Alpine)
^^^^^^^^^^^^^^^

Using an existing image
=============

https://hub.docker.com/r/nethermind/nethermind/

The image tagged as ``latest`` or tag e.g. ``1.5.7`` are ubuntu based. We highly encourage to use a lighter container which is Alpine based, tagged as ``alpine`` (Possible benefit of less memory consumption).

Docker pull command::

    docker pull nethermind/nethermind

In order to start Nethermind.Runner, simply run::

    docker run -it nethermind/nethermind

You can use ``--help`` to get a list of possible start parameters for Nethermind::

    docker run -it nethermind/nethermind --help

To enable JSON RPC, publish port 8545 and set NETHERMIND_JSONRPCCONFIG_ENABLED=true or --JsonRpc.Enabled true. If running from a VM you may want to expose JSON RPC to 
the outer world via NETHERMIND_JSONRPCCONFIG_HOST={hostmachine_ip} or --JsonRpc.Host {hostmachine_ip}:
    
    docker run -it -e NETHERMIND_JSONRPCCONFIG_ENABLED=true -e NETHERMIND_NETWORKCONFIG_P2PPORT=30312 -e NETHERMIND_NETWORKCONFIG_DISCOVERYPORT=30312 nethermind/nethermind

    or

    docker run -it --network host nethermind/nethermind --JsonRpc.Enabled true --Network.DiscoveryPort 30312 --Network.P2PPort 30312


To switch the network, set NETHERMIND_CONFIG variable (default value is mainnet).

Available configurations

- mainnet
- goerli
- rinkeby
- ropsten
- xdai
- poacore
- sokol
- volta

For example::

    docker run -it -e NETHERMIND_CONFIG=goerli nethermind/nethermind

It's also possible to modify each configuration property via environment variable, using a simple convention::
    
    NETHERMIND_{MODULE}CONFIG_{PROPERTY}

For example::

    NETHERMIND_INITCONFIG_NETWORKENABLED=true

You can find all of the possible settings `here <https://github.com/NethermindEth/nethermind/tree/master/src/Nethermind/Nethermind.Runner/configs/>`_.

To fully override a configuration file, you need to use a volume::

    -v $/home/mainnet.cfg:/nethermind/configs/mainnet.cfg

In order to map an internal database to a local volume, you need to map /nethermind/nethermind_db/::

    -v /tmp/nethermind_db:/nethermind/nethermind_db
    
    

Building an image
=============

`Dockerfile` can be found in the repository `root <https://github.com/NethermindEth/nethermind>`_ and it comes with 2 flavors:

-  `Dockerfile <https://github.com/NethermindEth/nethermind/blob/master/Dockerfile>`_ - requires to have a cloned repository in order to build an image.
-  `Dockerfile_full <https://github.com/NethermindEth/nethermind/blob/master/Dockerfile_full>`_ - doesn't require to have a cloned repository, as it will download it during the first step.


In order to build the images, run either:: 

    docker build -t nethermind .
    
or::

    docker build -f Dockerfile_full -t nethermind .

Depending on the chosen version.

Example usage of the locally created docker-image::

    docker run -it -e NETHERMIND_CONFIG=goerli nethermind .

Docker (Alpine ARM64)
^^^^^^^^^^^^^^^^^^^^^

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

    docker run -it --volume /var/lib/nethermind/mainnet:/data nethermind/nethermind:arm64 --help

The path ``/var/lib/nethermind/mainnet`` is just a recommendation and can be changed.

To use an existing database or configuration, you need to make the files accessible for the user inside the container. (uid/gid 1337)

Examples
========

Sync with mainnet
-----------------
::

    docker run -it --network host --volume /var/lib/nethermind/mainnet:/data nethermind/nethermind:arm64

Enable JSON-RPC
---------------
::

    docker run -it --network host --volume /var/lib/nethermind/mainnet:/data nethermind/nethermind:arm64 --JsonRpc.Enabled true
    
The default JSON-RPC port is 8545.

Sync another network
--------------------

To switch the network, set NETHERMIND_CONFIG variable (default value is mainnet)
or use --config flag e.g. --config goerli

Available configurations

- mainnet
- goerli
- rinkeby
- ropsten
- xdai
- poacore
- sokol
- volta

::

    docker run -it --network host --volume /var/lib/nethermind/goerli:/data nethermind/nethermind --config goerli

Run as daemon and start on (re)boot
---------------------------------

You can give your container a name, run it in the background and enable automatic restarts.

::

    docker run -d --name nethermind --restart always --network host --volume /var/lib/nethermind/mainnet:/data --stop-timeout 30 nethermind/nethermind:arm64
	
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
    docker pull nethermind/nethermind:arm64
    docker run -d --name nethermind --restart always --network host --volume /var/lib/nethermind/mainnet:/data --stop-timeout 30 nethermind/nethermind:arm64

Advanced docker usage
---------------------

More information about docker is available at: https://docs.docker.com

Build the image yourself
========================
::

    git clone https://github.com/NethermindEth/nethermind
    docker build nethermind -f nethermind/Dockerfile_alpine_arm64 -t nethermind
    docker run -it nethermind --help


Docker (ARM32)
^^^^^^^^^^^^^^

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