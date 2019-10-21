Docker
******

Using an existing image
=============

https://hub.docker.com/r/nethermind/nethermind/

Docker pull command::

    docker pull nethermind/nethermind

In order to start Nethermind.Runner, simply run::

    docker run -it nethermind/nethermind

To enable JSON RPC, publish port 8545 and set NETHERMIND_INITCONFIG_JSONRPCENABLED=true::
    
    docker run -it -p 8545:8545 -p 30312:30312 -e NETHERMIND_INITCONFIG_JSONRPCENABLED=true nethermind/nethermind

To switch the network, set NETHERMIND_CONFIG variable (default value is mainnet).

Available configurations

- mainnet
- goerli
- rinkeby
- ropsten

For example::

    docker run -it -e NETHERMIND_CONFIG=goerli nethermind/nethermind

It's possible to modify each configuration property via environment variable, using a simple convention::
    
    NETHERMIND_{MODULE}_{PROPERTY}

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

