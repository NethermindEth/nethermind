# Nethermind.Kademlia

Generic Kademlia routing-table and lookup primitives used by Nethermind peer discovery.

The package does not own transport, authentication, node records, or peer admission. It gives a protocol implementation the shared table, lookup, node-health, and random-walk discovery mechanics while the caller supplies key hashing, XOR-distance operations, and wire messages.

## Install

```bash
dotnet add package Nethermind.Kademlia
```

## Main types

- `IKademlia<TKey, TNode>` is the routing table and lookup facade.
- `Kademlia<TKey, TNode, TKadKey>` is the default implementation.
- `KBucketTree<TNode, TKadKey>` stores nodes in Kademlia buckets.
- `LookupKNearestNeighbour<TKey, TNode, TKadKey>` runs iterative closest-node lookups.
- `NodeHealthTracker<TKey, TNode, TKadKey>` tracks request failures and bucket refresh pings.
- `RandomWalkKademliaDiscovery<TKey, TNode, TKadKey>` streams candidates from active random lookups.

## Integration surface

Provide these protocol-specific pieces:

- `IKeyOperator<TKey, TNode, TKadKey>` maps your node and lookup key types into the Kademlia key space.
- `IKademliaDistance<TKadKey>` implements XOR log distance, key comparison by distance, and bit operations for the key type.
- `IKademliaMessageSender<TKey, TNode>` sends `Ping` and `FindNeighbours` over your protocol transport.
- `KademliaConfig<TNode>` sets the local node, bootnodes, k-bucket size, lookup parallelism, and refresh timing.

## Composition

```csharp
KademliaConfig<MyNode> config = new()
{
    CurrentNodeId = selfNode,
    BootNodes = bootNodes,
};

IKeyOperator<MyKey, MyNode, MyKadKey> keyOperator = new MyKeyOperator();
IKademliaDistance<MyKadKey> distance = new MyKadKeyDistance();
IKademliaMessageSender<MyKey, MyNode> sender = new MyKadMessageSender();
ILoggerFactory loggerFactory = NullLoggerFactory.Instance;

INodeHashProvider<MyNode, MyKadKey> nodeHashProvider =
    new FromKeyNodeHashProvider<MyKey, MyNode, MyKadKey>(keyOperator);
IRoutingTable<MyNode, MyKadKey> routingTable =
    new KBucketTree<MyNode, MyKadKey>(config, nodeHashProvider, distance, loggerFactory);
NodeHealthTracker<MyKey, MyNode, MyKadKey> healthTracker =
    new NodeHealthTracker<MyKey, MyNode, MyKadKey>(config, routingTable, nodeHashProvider, sender, loggerFactory);
ILookupAlgo<MyNode, MyKadKey> lookup =
    new LookupKNearestNeighbour<MyKey, MyNode, MyKadKey>(routingTable, nodeHashProvider, distance, healthTracker, config, loggerFactory);

IKademlia<MyKey, MyNode> kademlia =
    new Kademlia<MyKey, MyNode, MyKadKey>(keyOperator, sender, routingTable, lookup, healthTracker, config, loggerFactory);
IKademliaDiscovery<MyKey, MyNode> discovery =
    new RandomWalkKademliaDiscovery<MyKey, MyNode, MyKadKey>(kademlia, keyOperator, distance, config, loggerFactory);
```

Call `AddOrRefresh` when an authenticated node sends a valid protocol message, and call `Remove` when a node must be dropped from the table. Start periodic bootstrap and bucket refresh with `Run(token)`.

Use `LookupNodesClosest(key, token)` when the caller needs the final closest set. Use `LookupNodes(key, token, maxResults)` or `IKademliaDiscovery<TKey, TNode>.DiscoverNodes(...)` when the caller wants candidates streamed as soon as lookups find them.

Dispose `NodeHealthTracker<TKey, TNode, TKadKey>` when the host shuts down, or let the dependency-injection container that owns it dispose it.
