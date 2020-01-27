Web3.py
*******

It is possible to connect to the Nethermind node using web3.py (python web3.js implementation).
You will need to have web3.py installed using following guides:

- https://github.com/ethereum/web3.py
- https://web3py.readthedocs.io/en/stable/quickstart.html

You may use below script in order to check your connection.
Please make sure you have enabled JSON RPC module, this can be done by passing flag ``--JsonRpc.Enabled true`` to either Nethermind.Launcher or Nethermind.Runner

::

 from web3.auto import w3

 connected = w3.isConnected()
 print(connected)

 if connected and w3.clientVersion.startswith('Nethermind'):
     client = w3.clientVersion
     print(client)

 else:
     client = None
     print(client)

You should see the following output (depends on the node version):

::

 True
 Nethermind/v1.4.8-13-5c66dcdf6-20200120/X64-Linux 5.3.2-050302-generic/Core3.1.1