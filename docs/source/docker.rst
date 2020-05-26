Docker
******

Docker repository can be found  `here <https://hub.docker.com/repository/docker/nethermind/nethermind>`_.

We are currently supporting docker images for the following CPU architectures:

- `AMD64 Docker (Debian/Alpine) <https://nethermind.readthedocs.io/en/latest/docker.html#docker-debian-alpine>`_.
- `ARM64 Docker (Debian/Alpine) <https://nethermind.readthedocs.io/en/latest/docker.html#docker-debian-alpine-arm64>`_.
- `ARM32 Docker (Debian) <https://nethermind.readthedocs.io/en/latest/docker.html#docker-debian-arm32>`_.

Docker (Debian/Alpine)
^^^^^^^^^^^^^^^^^^^^^^

Using an existing image
=======================

Available tags:
---------------

- ``latest`` The latest Nethermind Debian based image
- ``alpine`` The latest Nethermind Alpine based image
- ``{tag}`` e.g. ``1.5.8`` Current or past version of Nethermind Debian based image `tags history <https://github.com/NethermindEth/nethermind/tags>`_. 
- ``{tag}-alpine`` e.g. ``1.5.8-alpine`` Current or past version of Nethermind Alpine based image `tags history <https://github.com/NethermindEth/nethermind/tags>`_.

We highly encourage to use a lighter container which is Alpine based, tagged as ``alpine`` or ``1.5.8-alpine`` (Possible benefit of less memory consumption).

Running Nethermind
------------------

Docker pull command::

    docker pull nethermind/nethermind:alpine

In order to start **Nethermind.Runner**, simply run::

    docker run -it nethermind/nethermind:alpine

You can use ``--help`` to get a list of possible start parameters for Nethermind or you can find them all `here <https://github.com/NethermindEth/nethermind/tree/master/src/Nethermind/Nethermind.Runner/configs/>`_.
::

    docker run -it nethermind/nethermind:alpine --help
    
It's possible to modify each configuration property via environment variable, using a simple convention::
    
    NETHERMIND_{MODULE}CONFIG_{PROPERTY} or --{Module}.{Property}

For example::

    NETHERMIND_INITCONFIG_ISMINING=true or --Init.IsMining

Environment variables are to be passed before docker image tag while parameteres should be passed right after the docker image tag.

JSON RPC
--------

To enable JSON RPC, share the host’s networking namespace with ``--network host`` and set ``NETHERMIND_JSONRPCCONFIG_ENABLED=true`` or ``--JsonRpc.Enabled true``. To change port simply pass ``NETHERMIND_JSONRPCCONFIG_PORT=8550`` or ``--JsonRpc.Port 8550``.

If running locally::

    docker run -it --network host nethermind/nethermind:alpine --JsonRpc.Enabled true

    or with port-mapping

    docker run -it -p 8545:8545 nethermind/nethermind:alpine --JsonRpc.Enabled true --JsonRpc.Host 0.0.0.0

If running from a VM you may want to expose JSON RPC to the outer world via ``NETHERMIND_JSONRPCCONFIG_HOST={hostmachine_ip}`` or ``--JsonRpc.Host {hostmachine_ip}`` (``127.0.0.1`` is set by default). You may try setting ``--JsonRpc.Host 0.0.0.0`` if you still can not connect with JSON RPC::
    
    docker run -it -e NETHERMIND_JSONRPCCONFIG_ENABLED=true -e NETHERMIND_JSONRPCCONFIG_HOST={hostmachine_ip} nethermind/nethermind:alpine

Available configurations
------------------------

To switch the network, set ``NETHERMIND_CONFIG`` variable (default value is ``mainnet``) or use ``--config {network}`` flag.

- ``mainnet``
- ``goerli``
- ``rinkeby``
- ``ropsten``
- ``xdai``
- ``poacore``
- ``sokol``
- ``volta``

For example::

    docker run -it -e NETHERMIND_CONFIG=goerli nethermind/nethermind:alpine
  
Volumes
-------

To fully override a configuration file, you need to use a volume::

    -v /home/user/mainnet.cfg:/nethermind/configs/mainnet.cfg

In order to map an internal database to a local volume, you need to map /nethermind/nethermind_db/::

    -v /home/user/nethermind_db:/nethermind/nethermind_db

Logs can be mapped with the following command::

    -v /home/user/logs:/nethermind/logs

Keystore::

    -v /home/user/keystore:/nethermind/keystore

Nlog::

    -v /home/user/NLog.config:/nethermind/NLog.config

More detailed NLog configuration can be found `here <https://github.com/NLog/NLog/wiki/Configuration-file>`_.

Building an image
=================

`Dockerfile` can be found in the repository `root <https://github.com/NethermindEth/nethermind>`_ and it comes with 3 flavors:

-  `Dockerfile <https://github.com/NethermindEth/nethermind/blob/master/Dockerfile>`_ - requires to have a cloned repository in order to build a Debian based image.
-  `Dockerfile_alpine <https://github.com/NethermindEth/nethermind/blob/master/Dockerfile_alpine>`_ - requires to have a cloned repository in order to build an Alpine based image.
-  `Dockerfile_full <https://github.com/NethermindEth/nethermind/blob/master/Dockerfile_full>`_ - doesn't require to have a cloned repository, as it will download it during the first step.


In order to build the images, run either:: 

    docker build -t nethermind .
    
or::

    docker build -f Dockerfile_full -t nethermind .

Depending on the chosen version.

Example usage of the locally created docker-image::

    docker run -it nethermind --config goerli

Docker (Debian/Alpine ARM64)
^^^^^^^^^^^^^^^^^^^^^^^^^^^^

Available tags
==============

- ``arm64`` The latest Nethermind ARM64 Debian based image
- ``alpine-arm64`` The latest Nethermind ARM64 Alpine based image
- ``{tag}-arm64`` e.g. ``1.5.8-arm64`` Current or past version of Nethermind ARM64 Debian based image `tags history <https://github.com/NethermindEth/nethermind/tags>`_. 
- ``{tag}-alpine-arm64`` e.g. ``1.5.8-alpine-arm64`` Current or past version of Nethermind ARM64 Alpine based image `tags history <https://github.com/NethermindEth/nethermind/tags>`_.

We highly encourage to use a lighter container which is Alpine based, tagged as ``alpine-arm64`` or ``1.5.8-alpine-arm64`` (Possible benefit of less memory consumption).

Running instructions are just like described `here <https://nethermind.readthedocs.io/en/latest/docker.html#docker-debian-alpine>`_. The only thing that changes is the ``tag`` specified for ARM64 platform.

Dockerfiles
===========

-  `Dockerfile_arm64 <https://github.com/NethermindEth/nethermind/blob/master/Dockerfile_arm64>`_ - requires to have a cloned repository in order to build an ARM64 Debian based image.

-  `Dockerfile_alpine_arm64 <https://github.com/NethermindEth/nethermind/blob/master/Dockerfile_alpine_arm64>`_ - requires to have a cloned repository in order to build an ARM64 Alpine based image.
    
Docker (Debian ARM32)
^^^^^^^^^^^^^^^^^^^^^

Available tags
==============

- ``arm32`` The latest Nethermind ARM32 Debian based image
- ``{tag}-arm32`` e.g. ``1.5.8-arm32`` Current or past version of Nethermind ARM32 Debian based image `tags history <https://github.com/NethermindEth/nethermind/tags>`_. 

Running instructions are just like described `here <https://nethermind.readthedocs.io/en/latest/docker.html#docker-debian-alpine>`_. The only thing that changes is the ``tag`` specified for ARM32 platform.

The arm32 version is not yet available as it is not supported for now (track here: https://github.com/dotnet/dotnet-docker/issues/1059).

Dockerfiles
===========

-  `Dockerfile_arm32 <https://github.com/NethermindEth/nethermind/blob/master/Dockerfile_arm32>`_ - requires to have a cloned repository in order to build an ARM32 Debian based image.