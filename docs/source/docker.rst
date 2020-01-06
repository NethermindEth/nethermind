Docker (Ubuntu)
******

Using an existing image
=============

https://hub.docker.com/r/nethermind/nethermind/

Docker pull command::

    docker pull nethermind/nethermind

In order to start Nethermind.Runner, simply run::

    docker run -it nethermind/nethermind

You can use ``--help`` to get a list of possible start parameters for Nethermind::

    docker run -it nethermind/nethermind --help

To enable JSON RPC, publish port 8545 and set NETHERMIND_JSONRPCCONFIG_ENABLED=true and NETHERMIND_URL=http://*:8545
or --JsonRpc.Enabled true --JsonRpc.Host 0.0.0.0::
    
    docker run -it -e NETHERMIND_URL=http://*:8545 -e NETHERMIND_JSONRPCCONFIG_ENABLED=true -e NETHERMIND_NETWORKCONFIG_P2PPORT=30312 -e NETHERMIND_NETWORKCONFIG_DISCOVERYPORT=30312 nethermind/nethermind

    or

    docker run -it --network host nethermind/nethermind --JsonRpc.Enabled true --Network.DiscoveryPort 30312 --Network.P2PPort 30312


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

