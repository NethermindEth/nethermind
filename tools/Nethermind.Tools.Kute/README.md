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

Some typical usages are as follows:

### Use all messages in the folder `/rpc-logs`

```
-i /rpc-logs -s keystore/jwt-secret
```

### Use a single messages file and emit results as JSON

```
-i /rpc.0 -s keystore/jwt-secret -o Json
```

### Use a single messages file and record all responses into a new file

```
-i /rpc.0 -s keystore/jwt-secret -r rpc.responses.txt
```

### Use a single message file, using only `engine` and `eth` methods

```
-i /rpc.0 -s keystore/jwt-secret -f engine, eth
```

### Use a single message file, using only the first 100 methods

```
-i /rpc.0 -s keystore/jwt-secret -f .*=100
```

### Use a single message file, using only the first 50 `engine_newPayloadV2` or `engine_newPayloadV3` methods

```
-i /rpc.0 -s keystore/jwt-secret -f engine_newPayloadV[23]=50
```

### Connect to a Nethermind Client running in a specific address and TTL

```
-i /rpc.0 -s keystore/jwt-secret -a http://192.168.1.100:8551 --ttl 30
```

### Run in "dry" mode (no communication with the Nethermind Client)

```
-i /rpc.0 -s keystore/jwt-secret -d
```

### A note on "progress"

Kute supports a `-p|--progress` flag that will show how many messages have been processed so far. This feature comes with a **big performance hit during startup** (it will not interfere with metrics though), so it's suggested to **not use it** unless it's required (ex. do not use it in automated environments like CI pipelines).

### TODO

There are some features that we might add in the future, if they end up being required:

- Validate the responses from the Nethermind Client (a "pedantic" mode)
- Other report outputs (ex. CSV)
