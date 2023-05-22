# Kute

Kute - /kjuːt/ - is a benchmarking tool developed at Nethermind to simulate an Ethereum Consensus Layer, expected to be used together with the Nethermind Client. The tool sends JSON-RPC messages to the Client and measures its performance.

## Prerequisites

This is a C# project and as such, it requires the [dotnet 7](https://dotnet.microsoft.com/en-us/download) SDK. Once installed, just run:

```
dotnet build [-c Release]
```

## Get JSON-RPC messages

To get real JSON-RPC messages, run the Nethermind Client using the `RpcRecorderState` state feature flag (see [JsonRpc module](https://docs.nethermind.io/nethermind/ethereum-client/configuration/jsonrpc)). The minimum required value is `Request` (`All` is also valid); this usually involves adding `--JsonRpc.RpcRecorderState <Request|All>` to your execution flags.

## Run

> We'll assume that the JWT secret used by the Nethermind Client is stored in `keystore/jwt-secret`.

Kute includes a built in help that can be accessed by the options `-h | --help`.

Some typical usages are as follow:

### Use all messages in the folder `/rpc-logs`

```
-i /rpc-logs -s keystore/jwt-secret
```

### Use a single messages file and emit results as JSON

```
-i /rpc-logs -s keystore/jwt-secret -o Json
```

### Use a single message file, using only `engine_*` and `eth_*` methods

```
-i /rpc.0 -s keystore/jwt-secret -f engine_*, eth_*
```

### Connect to a Nethermind Client running in a specific address

```
-i /rpc.0 -s keystore/jwt-secret -a http://192.168.1.100:8551
```

### Run in "dry" mode (no communication with the Nethermind Client)

```
-i /rpc.0 -s keystore/jwt-secret -d
```

### TODO

There are some features that we might add in the future, if they end up being required:

- Per method execution time
- Validate the responses from the Nethermind Client (a "pedantic" mode)
- Other report outputs (ex. CSV)
