Security
********

DO NOT use Nethermind wallet / signers for mainnet ETH handling!


JSON RPC endpoint (port 8545) should not be exposed publicly (should be behind the firewall).


Nethermind is thoroughly tested but the more popular it will get the more likely it will be the target of client-specific attacks. Generally you should always consider running backup client nodes implemented by a different team for any critical operations.


For non-mainnet signing you can use dev wallet configurations.


The private key from which the node ID is derived is stored on disk (not protected by password).
