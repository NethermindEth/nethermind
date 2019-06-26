Private Networks
****************

It is possible to configure a private network (private blockchain) using Nethermind nodes. Before you setup a private network you will need to make a few decisions. One of them is choosing a consensus protocol that will be used for securing the network.

Consensus Algorithms
====================

Nethermind supports the following consensus algorithms:

* PoW (ethash)
* PoA (Clique)
* None (NethDev)

PoW
^^^

In the proof of work consensus algorithm some of the nodes participate in a race to mine a new block by solving a mathematical puzzle. The difficulty of the puzzle (the amount of computation needed to find a solution) is adjusted so as to make blocks appear with some average frequency (every 15 seconds on the Ethereum mainnet). PoW is currently used to secure the public Ethereum mainnet and the Ropsten testnet. The only mining algorithm used for PoW in Ethereum is called ethash and is designed to be strict memory hard (see `Strict Memory Hard Hashing Functions <http://www.hashcash.org/papers/memohash.pdf>`_).

Clique
^^^^^^

Clique is an implementation of the Proof of Authority (PoA) consensus algorithm. The idea behind PoA is to choose a group of nodes within the network with authrity to create (seal) new blocks. In Clique such nodes are called signers and after the initial setup signers can vote to add new signers to the group or to remove any of the existing signers. Clique is currently supported by Geth, Parity, Nethermind and Pantheon and is used to secure the Goerli and Rinkeby testnets. 

Aura
^^^^

Aura is another implementation of the Proof of Authority consensus algorithm. It is currently only supported by the Parity Ethereum client. Aura is used to secure the Kovan testnet and the POA network.

NethDev
^^^^^^^

NethDev is a very simple consensus algorithm (or to better describe it - lack of consensus algorithm). NethDev can be used for setting up nodes for development and testing purposes. On the NethDev network any node can create a block with transactions and as long as it is a valid Ethereum block all other nodes will accept it.

Configuration
=============

Nethermind uses the same format chainspec files that can be used for setting up private networks in Parity. Only some of the elements of the chainspec files are supported by Nethermind:

* engine (to choose between Ethash, Clique or NethDev)
* genesis block spec
* initial account allocations
* bootnodes

The following elements are NOT supported at the moment:

* hardcoded sync block hashes
* precompile definitions and precompile activation blocks

You can find below links to some of the chainspec files used to define arious Ethereum networks:

* `foundation <https://github.com/NethermindEth/nethermind/blob/09389fc28b37605acc5eaed764d3e973969fe319/src/Nethermind/Chains/foundation.json>`_ - the public Ethereum mainnet
* `rinkeby <https://github.com/NethermindEth/nethermind/blob/09389fc28b37605acc5eaed764d3e973969fe319/src/Nethermind/Chains/rinkeby.json>`_ - the original Clique based testnet
* `goerli <https://github.com/NethermindEth/nethermind/blob/09389fc28b37605acc5eaed764d3e973969fe319/src/Nethermind/Chains/goerli.json>`_ - the new Clique based testnet
* `spaceneth <https://github.com/NethermindEth/nethermind/blob/09389fc28b37605acc5eaed764d3e973969fe319/src/Nethermind/Chains/spaceneth.json>`_ - a private network example with NethDev
