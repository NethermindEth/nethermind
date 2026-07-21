// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Find;

namespace Nethermind.JsonRpc.Modules.Admin;

[RpcModule(ModuleType.Admin)]
public interface IAdminRpcModule : IContextAwareRpcModule
{
    [JsonRpcMethod(Description = "Pauses local block processing. Blocks received from the network or consensus client are still queued but not processed; call `admin_resumeBlockProcessing` to process the accumulated backlog. Use `admin_isBlockProcessingPaused` to query the state. Intended for testing and diagnostics.",
        EdgeCaseHint = "Idempotent: pausing an already-paused processor is a no-op. While paused the node is reported as healthy (a deliberate pause is not treated as a processing stall).",
        ResponseDescription = "`true` if block processing is paused after the call (the request succeeded); `false` if it did not take effect.",
        ExampleResponse = "true",
        IsImplemented = true)]
    ResultWrapper<bool> admin_pauseBlockProcessing();

    [JsonRpcMethod(Description = "Resumes local block processing previously paused via `admin_pauseBlockProcessing`, processing any blocks accumulated in the queue. Intended for testing and diagnostics.",
        EdgeCaseHint = "Idempotent: resuming a processor that is not paused is a no-op.",
        ResponseDescription = "`true` when the resume request was accepted.",
        ExampleResponse = "true",
        IsImplemented = true)]
    ResultWrapper<bool> admin_resumeBlockProcessing();

    [JsonRpcMethod(Description = "Returns whether local block processing is currently paused.",
        ResponseDescription = "`true` if block processing is paused, `false` if running.",
        ExampleResponse = "false",
        IsImplemented = true)]
    ResultWrapper<bool> admin_isBlockProcessingPaused();

    [JsonRpcMethod(Description = "Adds the given node as a static peer. The connection is maintained for the lifetime of the process. Set `persistent` to also write the peer to static-nodes.json so it is restored on restart.",
        EdgeCaseHint = "Returns `true` even if the peer was already in the static set.",
        ResponseDescription = "`true` if the peer is in the static set after the call.",
        ExampleResponse = "true",
        IsImplemented = true)]
    Task<ResultWrapper<bool>> admin_addPeer(
        [JsonRpcParameter(Description = "Enode URL of the peer to add", ExampleValue = "\"enode://deed356ddcaa1eb33a859b818a134765fff2a3dd5cd5b3d6cbe08c9424dca53b947bdc1c64e6f1257e29bb2960ac0a4fb56e307f360b7f8d4ddf48024cdb9d68@85.221.141.144:30303\"")]
        string enode,
        [JsonRpcParameter(Description = "If `true`, also persist the peer to static-nodes.json so it is reloaded on the next start (optional, defaults to `false`).", ExampleValue = "true")]
        bool persistent = false);


    [JsonRpcMethod(Description = "Removes the given node from the static peer set and disconnects any active session. Set `persistent` to also remove the peer from static-nodes.json so it is not restored on restart.",
        EdgeCaseHint = "Returns `true` even if the peer was not present (idempotent).",
        ResponseDescription = "`true` if the input enode was valid and the removal was attempted.",
        ExampleResponse = "true",
        IsImplemented = true)]
    Task<ResultWrapper<bool>> admin_removePeer(
        [JsonRpcParameter(Description = "Enode URL of the peer to remove", ExampleValue = "\"enode://deed356ddcaa1eb33a859b818a134765fff2a3dd5cd5b3d6cbe08c9424dca53b947bdc1c64e6f1257e29bb2960ac0a4fb56e307f360b7f8d4ddf48024cdb9d68@85.221.141.144:30303\"")]
        string enode,
        [JsonRpcParameter(Description = "If `true`, also remove the peer from static-nodes.json so it is not reloaded on the next start (optional, defaults to `false`).", ExampleValue = "true")]
        bool persistent = false);


    [JsonRpcMethod(Description = "Displays a list of connected peers including information about them (`clientId`, `host`, `port`, `address`, `isBootnode`, `isStatic`, `enode`).",
        EdgeCaseHint = "",
        ResponseDescription = "List of connected peers including information",
        ExampleResponse = "[\n  {\n    \"clientId\": \"Nethermind/v1.10.33-1-5c4c185e8-20210310/X64-Linux/5.0.2\",\n    \"host\": \"94.237.54.114\",\n    \"port\": 30313,\n    \"address\": \"94.237.54.114:30313\",\n    \"isBootnode\": false,\n    \"isTrusted\": false,\n    \"isStatic\": false,\n    \"enode\": \"enode://46add44b9f13965f7b9875ac6b85f016f341012d84f975377573800a863526f4da19ae2c620ec73d11591fa9510e992ecc03ad0751f53cc02f7c7ed6d55c7291@94.237.54.114:30313\",\n    \"clientType\": \"Nethermind\",\n    \"ethDetails\": \"eth65\",\n    \"lastSignal\": \"03/11/2021 12:33:58\"\n  },\n  \n  (...)\n  \n]",
        IsImplemented = true)]
    ResultWrapper<PeerInfo[]> admin_peers(
        [JsonRpcParameter(Description = "If true, including `clientType`, `ethDetails` and `lastSignal` (optional)", ExampleValue = "true")]
        bool includeDetails = false);


    [JsonRpcMethod(Description = "Displays relevant information about this node.",
        EdgeCaseHint = "",
        ResponseDescription = "Information about this node",
        ExampleResponse = "{\n  \"enode\": \"enode://deed356ddcaa1eb33a859b818a134765fff2a3dd5cd5b3d6cbe08c9424dca53b947bdc1c64e6f1257e29bb2960ac0a4fb56e307f360b7f8d4ddf48024cdb9d68@85.221.141.144:30303\",\n  \"id\": \"b70bb308924de8247d73844f80561e488ae731105a6ef46004e4579edd4f378a\",\n  \"ip\": \"85.221.141.144\",\n  \"listenAddr\": \"85.221.141.144:30303\",\n  \"name\": \"Nethermind/v1.10.37-0-068e5c399-20210315/X64-Windows/5.0.3\",\n  \"ports\": {\n    \"discovery\": 30303,\n    \"listener\": 30303\n  },\n  \"protocols\": {\n    \"eth\": {\n      \"difficulty\": \"0x6372ca\",\n      \"genesis\": \"0xbf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1a\",\n      \"head\": \"0xf266b2639ef7e1db6ee769f7b161ef7eb2d74beb0ab8ffcd270036da04b41cd4\",\n      \"network\": \"0x5\"\n    }\n  }\n}",
        IsImplemented = true)]
    ResultWrapper<NodeInfo> admin_nodeInfo();


    [JsonRpcMethod(Description = "Returns the absolute path to the node's data directory.",
        ResponseDescription = "The data directory path as a string.",
        ExampleResponse = "\"/path/to/datadir\"",
        IsImplemented = true)]
    ResultWrapper<string> admin_dataDir();

    [JsonRpcMethod(Description = "[DEPRECATED]",
        IsImplemented = false)]
    ResultWrapper<bool> admin_setSolc();

    [JsonRpcMethod(Description = "True if state root for the block is available",
        EdgeCaseHint = "",
        ExampleResponse = "\"Starting\"",
        IsImplemented = true)]
    ResultWrapper<bool> admin_isStateRootAvailable(BlockParameter block);

    [JsonRpcMethod(Description = "Adds the given node to the trusted peer set so it can always connect even when slots are full. Set `persistent` to also write the peer to trusted-nodes.json so it is restored on restart.",
        EdgeCaseHint = "Returns `true` even if the peer was already trusted (idempotent).",
        ResponseDescription = "`true` if the peer is in the trusted set after the call.",
        ExampleResponse = "true",
        IsImplemented = true)]
    Task<ResultWrapper<bool>> admin_addTrustedPeer(
        [JsonRpcParameter(Description = "Enode URL of the peer to trust", ExampleValue = "\"enode://...\"")]
        string enode,
        [JsonRpcParameter(Description = "If `true`, also persist the peer to trusted-nodes.json so it is reloaded on the next start (optional, defaults to `false`).", ExampleValue = "true")]
        bool persistent = false);

    [JsonRpcMethod(Description = "Removes the given node from the trusted peer set. Set `persistent` to also remove the peer from trusted-nodes.json so it is not re-trusted on restart.",
        EdgeCaseHint = "Returns `true` even if the peer was not trusted (idempotent).",
        ResponseDescription = "`true` if the input enode was valid and the removal was attempted.",
        ExampleResponse = "true",
        IsImplemented = true)]
    Task<ResultWrapper<bool>> admin_removeTrustedPeer(
        [JsonRpcParameter(Description = "Enode URL of the peer to untrust", ExampleValue = "\"enode://...\"")]
        string enode,
        [JsonRpcParameter(Description = "If `true`, also remove the peer from trusted-nodes.json so it is not reloaded on the next start (optional, defaults to `false`).", ExampleValue = "true")]
        bool persistent = false);

    [JsonRpcMethod(Description = "Subscribes to a particular event over WebSocket. For every event that matches the subscription, a notification with event details and subscription id is sent to a client.", IsImplemented = true, IsSharable = false, Availability = RpcEndpoint.All & ~RpcEndpoint.Http)]
    ResultWrapper<string> admin_subscribe(string subscriptionName, string? args = null);
    [JsonRpcMethod(Description = "Unsubscribes from a subscription.", IsImplemented = true, IsSharable = false, Availability = RpcEndpoint.All & ~RpcEndpoint.Http)]
    ResultWrapper<bool> admin_unsubscribe(string subscriptionId);
}
