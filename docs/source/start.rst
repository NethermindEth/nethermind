Getting Started
***************

Here is a quick start example for Goerli testnet Nethermind sync.

Setup a VM 
^^^^^^^^^^

If you are not launching on your laptop/desktop then you will probably want to setup a cloud VM.
You cand more info about setting up everything with selected `cloud providers <https://nethermind.readthedocs.io/en/latest/cloud.html>`_.
Make sure that you configure `firewall <https://nethermind.readthedocs.io/en/latest/firewall_configuration.html>`_ properly.
You can check `supported operating systems and architectures <https://nethermind.readthedocs.io/en/latest/platforms.html>`_ and `hardware requirements <https://nethermind.readthedocs.io/en/latest/hardware_requirements.html>`_

Download 
^^^^^^^^

You need to download the Nethermind package first (read `download instructions <https://nethermind.readthedocs.io/en/latest/download.html>`_.)

Launch
^^^^^^

After launching Nethermind via ./Nethermind.Launcher you need to choose the Node to start first

.. image:: start/launcher.png


Then select Goerli testnet

.. image:: start/goerli-choice.png


Then select fast sync mode

.. image:: start/fast-sync-choice.png


Then ensure that JSON RPC is enabled

.. image:: start/json-choice.png


Then skip the ethstats registration (you may need to press ENTER twice after that)

.. image:: start/ethstats-choice.png


After the node starts you will see some initial info about the node and then the sync will start.
Goerli fast sync uses a 'fast blocks' sync mode initially. The fast blocks sync picks some known pivot block from the past and downloads headers, bodies, and receipts downwards all the way to genesis block. The console display shows the number growing from 0 to pivot but this is just to make the display more user-friendly.

You will see some information about the sync progress, like below:

1. Shows the number of already downloaded headers, bodies and receipts out of all to be downloaded in the fast blocks stage.
2. Shows the current queue of already downloaded blocks, headers and receipts waiting for being saved to the database.
3. Shows the current download speed (blocks per second - bps).
4. Shows the average download speed (blocks per second - bps).

.. image:: start/fast-blocks-sync-annotated.png


When the fast blocks stage finishes there will be some period of downloading blocks between the pivot and the latest blocks which will have some additional info:

1. Shows the last entry from the fast blocks stage.
2. Shows the mode transition moment.
3. Displays the speed (in blocks per second) of all headers, bodies and receipts at the same time.
4. Additional info will appear every 30000 blocks with information about the Goerli epoch being stored.

.. image:: start/fast-sync-annotated.png


After the fast sync part finished the node will transition to the state sync stage when the state trie is being downloaded. Much information is displayed about the progress as this process may take a long time on mainnet (a few hours).

1. Total elapsed time in state sync is displayed.
2. Total downloaded DB size is displayed (on mainnet the sync finishes around 34GB in March 2020, on Goerli around 800MB).
3. Download speed in kilobytes per second is displayed.
4. Occasionally a queue review message may appear (you can ignore it).
5. 'P' stands for the number of current parallel requests that the node is waiting for.
6. 'acc' stands for the number of accounts data downloaded already.
7. 'nodes' stands for the number of Patricia trie nodes downloaded by the sync process.
8. 'db_delay' shows the time spent in the DB write / read access. The higher the value the worse. It may get much worse if you restart the node during the sync process as we need to recreate some caches then by reading data from the DB.
9. Occasionally we display additional info about the health of requests and caches that you may ignore unless you need to report any issues.

.. image:: start/state-sync-annotated.png


When the state sync is nearing completion you may see a series of branch sync information realoading many times from 0% to nearly 100%. This is the node trying to retrieve the few remaining state nodes and progressing with the head block rapidly:

.. image:: start/branch-sync.png


At some point the entire state is downloaded and the node enters the full sync mode and will allow you to issue CLI / Web3 queries and send / receive transactions.

1. The root is saved at the moment when the entire Patricia trie is downloaded.
2. We also clearly state that the node transitions to the full sync.
3. When you see the block being processed then you are in the full sync and the newly arrived block is being calculated.
4. Every two minutes you will see a summary of connected peers with their client version, IP address, highest synced block, and data download speeds.

.. image:: start/full-sync-annotated.png


Now you can launch CLI in the separate process (launching Nethermind.Launcher again and selecting CLI instead of Node).
You can read more about `CLI here <https://nethermind.readthedocs.io/en/latest/cli.html>`_.)


You can also connect to the node from Metamask by selecting localhost:8545 from the Metamask network list.

.. image:: metamask/localhost.png
