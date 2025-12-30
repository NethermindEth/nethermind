# Engine API Proxy

A proxy server that sits between Consensus Layer (CL) and Execution Layer (EL) clients to intercept and enhance Engine API messages.

## Architecture

The proxy is built with a modular, maintainable architecture:

### Core Components

1. **ProxyServer**: Main entry point and HTTP server for handling JSON-RPC requests
2. **MessageQueue**: Manages request sequencing and pausing/resuming processing
3. **PayloadTracker**: Tracks payload IDs and their associated block hashes
4. **RequestOrchestrator**: Coordinates the validation flow between components

### Request Handlers

1. **ForkChoiceUpdatedHandler**: Processes engine_forkchoiceUpdated* requests
2. **NewPayloadHandler**: Processes engine_newPayload* requests  
3. **GetPayloadHandler**: Processes engine_getPayload* requests
4. **DefaultRequestHandler**: Processes all other request types

### Utilities

1. **RequestForwarder**: Handles forwarding requests to the execution client
2. **BlockDataFetcher**: Retrieves block data from the execution client
3. **PayloadAttributesGenerator**: Generates payload attributes for validation

## Validation Flows

The proxy supports two validation modes:

1. **FCU Mode**: Validates through the ForkChoiceUpdated pipeline
2. **NewPayload Mode**: Validates through the NewPayload pipeline

Both modes intercept requests, perform validation, and ensure proper execution client responses.

## Configuration

Key configuration options:

- **ValidationMode**: FCU or NewPayload
- **ValidateAllBlocks**: Whether to validate all blocks or only specific ones
- **ExecutionClientEndpoint**: The URL of the execution client
- **ListenPort**: The port on which the proxy listens for requests

## Usage

```bash
dotnet run --project tools/EngineApiProxy -- --ec.url=http://localhost:8545 --listen.port=8551
```

## Design Principles

1. **Single Responsibility**: Each class has a well-defined responsibility
2. **Dependency Injection**: Services are injected where needed
3. **Error Handling**: Comprehensive try/catch with proper logging
4. **Testability**: Components are designed to be testable in isolation
5. **Logging**: Detailed logging at appropriate levels throughout the code

## Overview

The Engine API Proxy sits between the Consensus Client (CC) and Execution Client (EC), intercepting Engine API calls. It implements a specific workflow for `engine_forkchoiceUpdated`, `engine_newPayload`, and `engine_getPayload` messages to ensure optimized processing sequence.

## Building

### From Source

```bash
# Clone the repository
git clone https://github.com/nethermindeth/nethermind.git

# Build the Engine API Proxy
cd tools/EngineApiProxy
dotnet build -c Release
```

### Using Docker

```bash
# Build the Docker image
docker build -f tools/EngineApiProxy/Dockerfile -t nethermindeth/engine-api-proxy:latest .
```

## Running

### Command-Line Options

- `--ec-endpoint` or `-e`: The URL of the execution client API endpoint (required)
- `--cl-endpoint` or `-c`: The URL of the consensus client API endpoint (optional)
- `--port` or `-p`: The port to listen for consensus client requests (default: 8551)
- `--log-level` or `-l`: Log level (Trace, Debug, Info, Warn, Error) (default: Info)
- `--validate-all-blocks`: Enable validation for all blocks, even those where CL doesn't request validation
- `--fee-recipient`: Default fee recipient address for generated payload attributes (default: 0x0000000000000000000000000000000000000000)
- `--validation-mode`: Mode for block validation (Fcu or NewPayload) (default: NewPayload)
- `--get-payload-method`: Engine API method to use when getting payloads for validation (default: engine_getPayloadV4)

### Examples

#### Running from compiled binary

```bash
# Run with basic settings
dotnet run -c Release -- -e http://localhost:8551 -p 9551 -l Debug

# With Consensus Client endpoint for direct CL data access
dotnet run -c Release -- -e http://localhost:8551 -c http://localhost:4000 -p 9551

# With full option names
dotnet run -c Release -- --ec-endpoint http://localhost:8551 --port 9551 --log-level Debug --cl-endpoint http://localhost:4000

# With auto-validation of all blocks enabled
dotnet run -c Release -- -e http://localhost:8551 -p 9551 --validate-all-blocks

# Using Fcu validation mode instead of NewPayload
dotnet run -c Release -- -e http://localhost:8551 -p 9551 --validate-all-blocks --validation-mode Fcu

# Using a specific engine_getPayload version
dotnet run -c Release -- -e http://localhost:8551 -p 9551 --get-payload-method engine_getPayloadV3
```

#### Running with Docker

```bash
# Basic usage
docker run -p 9551:9551 nethermindeth/engine-api-proxy:latest -e http://execution-client:8551 -p 9551

# With auto-validation of all blocks enabled
docker run -p 9551:9551 nethermindeth/engine-api-proxy:latest -e http://execution-client:8551 -p 9551 --validate-all-blocks

# Using Fcu validation mode
docker run -p 9551:9551 nethermindeth/engine-api-proxy:latest -e http://execution-client:8551 -p 9551 --validate-all-blocks --validation-mode Fcu

# Using a specific engine_getPayload version
docker run -p 9551:9551 nethermindeth/engine-api-proxy:latest -e http://execution-client:8551 -p 9551 --get-payload-method engine_getPayloadV3
```

## Consensus Client Integration

When a consensus client endpoint is configured using the `--cl-endpoint` or `-c` parameter, the proxy can directly fetch data from the consensus client. This enables additional features and provides access to beacon chain data.

### Data Retrieval

The BlockDataFetcher service uses the following Beacon API endpoints to communicate with the consensus client:

- `/eth/v1/beacon/headers/head`: Used to get the current head beacon block header
- `/eth/v1/beacon/blocks/{block_id}`: Used to fetch full block data for a specific block

This is useful for internal processing within the proxy, such as tracking beacon chain state or building enhanced functionality.

## Configuration with Clients

### Consensus Client Configuration

Configure your Consensus Client to connect to the proxy instead of directly to the Execution Client:

```
# Example Lighthouse configuration
--execution-endpoint http://localhost:9551

# Example Prysm configuration
--execution-endpoint=http://localhost:9551
```

### Execution Client Configuration

No special configuration is needed for the Execution Client. The proxy will forward requests to the standard Engine API endpoint.

## Development

The project consists of several key components:

- `ProxyServer.cs`: Main HTTP server and request router
- `MessageQueue.cs`: Handles message queueing and processing
- `PayloadTracker.cs`: Tracks relationships between payloads and head blocks
- `BlockDataFetcher.cs`: Fetches block data from the execution client
- `PayloadAttributesGenerator.cs`: Generates valid payload attributes
- `RequestOrchestrator.cs`: Orchestrates the validation flow

## Auto-Validation Feature

The auto-validation feature (`--validate-all-blocks`) allows the proxy to validate all blocks, including those where the Consensus Layer doesn't request validation (by sending null payload attributes).

### Validation Flow

Validation flow allows to generate validation events (FCU -> GetPayload -> NewPayload) for blocks that were validated by another consensus client.

It might be useful in the following cases:

1. To make sure that your execution client can build a valid block that another consensus client already validated
2. Catch and log bad blocks as soon as possible
3. Testing consensus implementations
4. Debug issues with block validation logic (TBD)

When enabled with `--validate-all-blocks`, the proxy will:

1. Detect FCU requests with null payload attributes
2. Generate valid payload attributes based on the current head block
3. Create and validate blocks (send `engine_getPayload` and `engine_newPayload` requests to EC)
4. Consensus Layer will not know about this, so it will proceed with the next block as usual

The tool did not modify existing blockchain and can be safely used in the production environment. But it is highly recommend to use `--validate-all-blocks` flag to enable the validation only on nodes that are not validator ones.

There are 2 flows of validation:

1. Validation after `engine_newPayload` request (`--validation-mode=NewPayload`)

In this flow CL sends `engine_newPayload` request to the proxy, proxy generates payload attributes based on the payload and sends `engine_forkChoiceUpdated` request to the execution client with payload attributes. Then EL will return PayloadID and proxy will use it to send `engine_getPayload` request to the execution client. After that proxy will generate synthetic `engine_newPayload` request and send it to the execution client.

With the flow, we will generate FCU based on the parent block, so the final "validated" block may be different from the original one. Since we hold `engine_newPayload` request and do not control FCU, flow will run against all `engine_newPayload` requests, so this may break validator node.

#### NewPayload Flow

```mermaid
sequenceDiagram
    participant CL as Consensus Client
    participant P as Proxy
    participant EC as Execution Client

    %% Step 7: Proceed a valid engine_newPayload (if any)
    CL->>P: (1) engine_newPayload (next block payload)
    
    Note over P: Get data from engine_newPayload and generate PayloadAttributes based on it
    %% Based on request (1) PR generates PayloadAttributes
    alt Validation enabled
 
    P->>P: (1.1) generate PayloadAttributes (based on previous block)
    Note over P: Append PayloadAttributes to FCU
    
    %% Step 2: PR sends generated request (1.1) to EL
    P->>EC: (2) engine_forkchoiceUpdated (forkchoiceState + PayloadAttributes)
    EC->>P: (2) engine_forkchoiceUpdated response (PayloadID)
       
    %% Step 3: getting PayloadID from EL (2), sending engine_getPayload with it
    Note over P: Validation start
    alt Validation
    P->>EC: (3) engine_getPayload (PayloadID)
    EC->>P: (3) engine_getPayload Response (execution payload)
    
    %% Step 4: Sending engine_newPayload with generated (3) payload
    P->>EC: (4) engine_newPayload(generated from getPayload response)
    EC->>P: (4) -- engine_newPayload response (save in case of error)
    end

    end
    %% Step 5: Resuming original engine_newPayload
    P->>EC: (1) engine_newPayload response (RESUME)
    P->>CL: (1) engine_newPayload response (Original response)
    Note over P: Process any new engine_forkchoiceUpdated messages

    %% Step 6: CL sends engine_forkChoiceUpdated based on original engine_newPayload response
    CL->>P: (5) engine_forkchoiceUpdated (with PayloadAttributes null)
    P->>EC: (5) engine_forkchoiceUpdated
    EC->>P: (5) engine_forkchoiceUpdated response
    P->>CL: (5) engine_forkchoiceUpdated response 

```

2.Validation of payloads based on `engine_forkchoiceUpdated` request (`--validation-mode=Fcu`)

The difference between this flow is that we are not waiting for `engine_newPayload` request from CL, but only for `engine_forkchoiceUpdated` with null payload attributes. The validation payload should be very close to the original one (since we are using the same `forkchoiceState`), but with different `blockHash` and `blockNumber`.

The flow may be safely used with both validator and non-validator nodes, since we are ignoring FCU requests when payload attributes are not null.

#### FCU Flow

```mermaid
sequenceDiagram
    participant CL as Consensus Client
    participant P as Proxy
    participant EC as Execution Client

    %% Step 1: CL sends engine_forkchoiceUpdated
    CL->>P: (1) engine_forkchoiceUpdated (with PayloadAttributes null)
    Note over P: Intercept & delay engine_forkchoiceUpdated
    
    %% Step 1.1: based on request (1) PR generates PayloadAttributes
    alt Validation enabled && PayloadAttributes null
    
    P->>P: (1.1) generate PayloadAttributes (based on the current head block)
    Note over P: Append PayloadAttributes to FCU
    
    %% Step 2: PR sends generated request (1.1) to EL
    P->>EC: (2) engine_forkchoiceUpdated (forkchoiceState + PayloadAttributes)
    EC->>P: (2) engine_forkchoiceUpdated response (PayloadID)
       
    %% Step 3: getting PayloadID from EL (2), sending engine_getPayload with it
    Note over P: Validation start
    alt Validation
    P->>EC: (3) engine_getPayload (PayloadID)
    EC->>P: (3) engine_getPayload Response (execution payload)
    
    %% Step 4: Sending engine_newPayload with generated (3) payload
    P->>EC: (4) engine_newPayload(generated from getPayload response)
    EC->>P: (4) -- engine_newPayload response (save in case of error)
    end

    end
    %% Step 5: Release blocked original FCU request (1)
    Note over P: Release FCU
    P->>EC: (1) engine_forkchoiceUpdated (resuming)
    EC->>P: (1) engine_forkchoiceUpdated response
    P->>CL: (1) engine_forkchoiceUpdated response 
    
    %% Step 6: Proceed a valid engine_newPayload (if any)
    CL->>P: (5) engine_newPayload (next block payload)
    P->>EC: (5) engine_newPayload response
    EC->>P: (5) engine_newPayload response
    P->>CL: (5) engine_newPayload response 
   
    Note over P: Process any new engine_forkchoiceUpdated messages


```

#### Differences between FCU and NewPayload flows

- they block (and then resume) different requests (`engine_forkchoiceUpdatedV3` vs `engine_newPayload`)
- in FCU flow payload we have already the payload we need to validate, in newPayload flow we need to generate it based on current state
- in FCU flow `engine_forkchoiceUpdatedV3` will be generated using `latestValidHash` from the current `engine_newPayload` request (because EL already has this block), in NewPayload flow we will use `latestValidHash` from the previous block (since current block is not exist in EL DB yet)
- thus, we may have different payloads in FCU and NewPayload flows, but FCU one will be more close to the original one
- in FCU we start validation only if we have null payload attributes, in NewPayload we will validate every payload (since there is no way to know if the payload was already validated by another CL before `engine_newPayload` request was sent)


## Testing with Kurtosis

1.Build docker image

```bash
docker build -f tools/EngineApiProxy/Dockerfile -t nethermindeth/engine-api-proxy:latest .
```

2.Clone repository with kurtosis scripts

```bash
git clone https://github.com/dmitriy-b/ethereum-package.git
cd ethereum-package
git checkout feat/el-proxy
```

3.Install kurtosis CLI https://docs.kurtosis.com/install/#ii-install-the-cli 

4.Update `el-proxy.yaml` with correct execution client endpoint

```
participants:
  - el_type: nethermind
    cl_type: lighthouse
    snooper_enabled: false
    # el_proxy_enabled: true
  - el_type: nethermind
    cl_type: lighthouse
    validator_count: 0
    use_separate_vc: true
    snooper_enabled: false
    el_proxy_enabled: true
```

Note: there should be at least 2 nodes with one validator. `el_proxy_enabled: true` should be set for one of them (not validator one) and `snooper_enabled` should be set to `false` for it.

5.Run kurtosis stack

```bash
kurtosis run . --args-file .github/tests/el-proxy.yaml --enclave testnet
```

6.Using hte script find out IP addresses and ids of cl, el and proxy containers 

```bash
python3 tools/EngineApiProxy/Scripts/inspect_containers.py 
```

You will get output like this:

```
Container Name: vc-1-nethermind-lighthouse--f65236fe1ca847219d06743d1b27b254
Container ID: 0ad50ac1ddd8
IP Address: 172.16.0.14
Beacon Nodes: http://172.16.0.12:4000
----------------------------------------
Container Name: cl-2-lighthouse-nethermind--c30ffb94d23b491cb74937b55098d3a2
Container ID: aad9e7a6f2c2
IP Address: 172.16.0.13
Execution Endpoints: http://172.16.0.11:9551
----------------------------------------
Container Name: cl-1-lighthouse-nethermind--22f7e291c6cb42ecaee2b288ae51ce04
Container ID: e86a6dd5b8e3
IP Address: 172.16.0.12
Execution Endpoints: http://172.16.0.9:8551
----------------------------------------
Container Name: el-proxy-2-nethermind--40c3d946b41f4b14ab4f34569d17a3cc
Container ID: 00cf58fd8a86
IP Address: 172.16.0.11
Transformed Args:
  EC_ENDPOINT: http://172.16.0.10:8551
  PORT: 9551
  VALIDATE_ALL_BLOCKS: None
  LOG_LEVEL: Info
----------------------------------------
Container Name: el-2-nethermind-lighthouse--f7905805d6804fee83796f14c9c228a4
Container ID: 1f4a966091ad
IP Address: 172.16.0.10
----------------------------------------
Container Name: el-1-nethermind-lighthouse--c43ec163f1b04c569d9bf4e49995ce32
Container ID: d43416e2d209
IP Address: 172.16.0.9
```

7.Check logs of proxy container

```bash
docker logs el-proxy-2-nethermind--40c3d946b41f4b14ab4f34569d17a3cc > proxy.txt
```

8.Check logs of consensus client

```bash
docker logs cl-1-lighthouse-nethermind--22f7e291c6cb42ecaee2b288ae51ce04 > consensus.txt
```

## Issues and Limitations

- "Blob versioned hashes do not match" error may happen on EC. Currently proxy doesn't support blob transactions. All such blocks will be skipped during the validation.
- Following message in EL logs:
  ```txt
  sedge-execution-client  | 10 Apr 16:05:25 | Non consecutive block commit. This is likely a reorg. Last block commit: 158346. New block commit: 158346.
  ```
  This is an expected and happens because we do not send FCU after the validation. So, CL doesn't not know about the block and we revert it on the EL side

## Troubleshooting

- Set log level to debug and check proxy logs for detailed information. Debug level can be set here https://github.com/dmitriy-b/ethereum-package/blob/4e3ca6d6b594a1e08c1b7441337256fd1cb01874/src/el-proxy/el_proxy_launcher.star#L56 
- Verify connectivity to both the Consensus Client and Execution Client
- Ensure the execution client endpoint URL is correctly formatted and accessible
- If using auto-validation, check that block data is being fetched successfully
- Using `tools/EngineApiProxy/Scripts/inspect_containers.py` script you can find out IP addresses and configuration of the proxy container
- "2025-04-11 14:56:38.2485|WARN|Validation flow failed, payloadId is empty. Seems like the node is not synced yet. " - this is expected and happens when CL is not synced yet. To check sync status do:
  ```
  curl -X GET "http://localhost:4000/eth/v1/node/syncing" -H "Accept: application/json"
  ```