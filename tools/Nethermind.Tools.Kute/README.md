# Kute

Kute - /kjuːt/ - is a benchmarking tool developed at Nethermind to simulate an Ethereum Consensus Layer, expected to be used together with the Nethermind Client. The tool sends JSON-RPC messages to the Client and measures its performance.

## Prerequisites

This is a C# project and as such, it requires the [dotnet 9](https://dotnet.microsoft.com/en-us/download) SDK. Once installed, just run:

```
dotnet build [-c Release]
```

## Get JSON-RPC messages

To get real JSON-RPC messages, run the Nethermind Client using the `RpcRecorderState` state feature flag (see [JsonRpc module](https://docs.nethermind.io/nethermind/ethereum-client/configuration/jsonrpc)). The minimum required value is `Request` (`All` is also valid); this usually involves adding `--JsonRpc.RpcRecorderState <Request|All>` to your execution flags.

## Run

> We'll assume that the JWT secret used by the Nethermind Client is stored in `/keystore/jwt-secret`.

Kute includes a built in help that can be accessed by the options `-h | --help`.

Some typical usages are as follows:

### Connect to a Nethermind Client running at a specific address using a single file

```
-a http://localhost:8551 -s /keystore/jwt-secret -i /rpc.0
```

### Use all messages in the directory `/rpc-logs`

```
-a http://localhost:8551 -s /keystore/jwt-secret -i /rpc-logs
```

### Use a single messages file and emit results as JSON

```
-a http://localhost:8551 -s /keystore/jwt-secret -i /rpc.0 -o Json
```

### Use a single messages file and record all responses into a new file

```
-a http://localhost:8551 -s /keystore/jwt-secret -i /rpc.0 -r rpc.responses.txt
```

### Use a single message file, using only `engine` and `eth` methods

```
-a http://localhost:8551 -s /keystore/jwt-secret -i /rpc.0 -f engine,eth
```

### Use a single message file, using only the first 100 methods

```
-a http://localhost:8551 -s /keystore/jwt-secret -i /rpc.0 -f .*=100
```

### Use a single message file, using only the first 50 `engine_newPayloadV2` or `engine_newPayloadV3` methods

```
-a http://localhost:8551 -s /keystore/jwt-secret -i /rpc.0 -f engine_newPayloadV[23]=50
```

### TODO

There are some features that we might add in the future, if they end up being required:

- Validate the responses from the Nethermind Client (a "pedantic" mode)
- Other report outputs (ex. CSV, Prometheus)
