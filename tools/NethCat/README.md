# NethCat

NethCat is a Nethermind network utilities CLI tool for network debugging and exploration.

## Building

```bash
cd tools/NethCat
dotnet build -c Release
```

## Usage

### Discovery v5

The `discv5` command provides utilities for interacting with the Discovery v5 protocol.

#### Discover Nodes

Discover nodes on the network using a random walk algorithm:

```bash
# Using mainnet bootnodes
NethCat discv5 discover --bootnodes "enr:-KG4QIBjOm7S1Zd..." --timeout 30

# With custom port
NethCat discv5 discover --bootnodes "enr:-KG4QIBjOm7S1Zd..." --port 30304

# With specific private key
NethCat discv5 discover --bootnodes "enr:-KG4QIBjOm7S1Zd..." --privatekey "0x..."

# Infinite discovery (Ctrl+C to stop)
NethCat discv5 discover --bootnodes "enr:-KG4QIBjOm7S1Zd..." --timeout 0

# With verbose logging
NethCat discv5 discover --bootnodes "enr:-KG4QIBjOm7S1Zd..." --loglevel Debug
```

##### Discover Options

- `-b, --bootnodes <enrs>` (required): Comma-separated list of bootnode ENRs or enodes
- `-p, --port <port>`: UDP port for discovery (default: 30303)
- `-t, --timeout <seconds>`: Discovery timeout in seconds, 0 for infinite (default: 60)
- `-k, --privatekey <hex>`: Private key hex (generates random if not specified)
- `-l, --loglevel <level>`: Log level - Trace, Debug, Info, Warn, Error (default: Info)

#### Connect to a Node

Connect to a single node and log all discv5 protocol messages:

```bash
# Connect to a node with trace logging
NethCat discv5 connect --node "enr:-KG4QIBjOm7S1Zd..." --loglevel Trace

# Connect for a specific duration (30 seconds)
NethCat discv5 connect --node "enr:-KG4QIBjOm7S1Zd..." --duration 30

# Connect with debug logging on custom port
NethCat discv5 connect --node "enr:-KG4QIBjOm7S1Zd..." --port 30304 --loglevel Debug
```

##### Connect Options

- `-n, --node <enr>` (required): Target node ENR or enode to connect to
- `-p, --port <port>`: UDP port for discovery (default: 30303)
- `-k, --privatekey <hex>`: Private key hex (generates random if not specified)
- `-l, --loglevel <level>`: Log level - Trace, Debug, Info, Warn, Error (default: Info)
- `-d, --duration <seconds>`: Duration to stay connected in seconds, 0 for infinite (default: 0)

