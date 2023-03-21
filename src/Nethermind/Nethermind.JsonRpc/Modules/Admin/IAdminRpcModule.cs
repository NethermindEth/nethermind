// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.FullPruning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Admin
{
    [RpcModule(ModuleType.Admin)]
    public interface IAdminRpcModule : IRpcModule
    {
        [JsonRpcMethod(Description = "Adds given node.",
            EdgeCaseHint = "",
            ResponseDescription = "Added node",
            ExampleResponse = "\"enode://deed356ddcaa1eb33a859b818a134765fff2a3dd5cd5b3d6cbe08c9424dca53b947bdc1c64e6f1257e29bb2960ac0a4fb56e307f360b7f8d4ddf48024cdb9d68@85.221.141.144:30303\"",
            IsImplemented = true)]
        Task<ResultWrapper<string>> admin_addPeer(
            [JsonRpcParameter(Description = "Given node", ExampleValue = "\"enode://deed356ddcaa1eb33a859b818a134765fff2a3dd5cd5b3d6cbe08c9424dca53b947bdc1c64e6f1257e29bb2960ac0a4fb56e307f360b7f8d4ddf48024cdb9d68@85.221.141.144:30303\"")]
            string enode,
            [JsonRpcParameter(Description = "Adding to static nodes if `true` (optional)", ExampleValue = "true")]
            bool addToStaticNodes = false);


        [JsonRpcMethod(Description = "Removes given node.",
            EdgeCaseHint = "",
            ResponseDescription = "Removed node",
            ExampleResponse = "\"enode://deed356ddcaa1eb33a859b818a134765fff2a3dd5cd5b3d6cbe08c9424dca53b947bdc1c64e6f1257e29bb2960ac0a4fb56e307f360b7f8d4ddf48024cdb9d68@85.221.141.144:30303\"",
            IsImplemented = true)]
        Task<ResultWrapper<string>> admin_removePeer(
            [JsonRpcParameter(Description = "Given node", ExampleValue = "\"enode://deed356ddcaa1eb33a859b818a134765fff2a3dd5cd5b3d6cbe08c9424dca53b947bdc1c64e6f1257e29bb2960ac0a4fb56e307f360b7f8d4ddf48024cdb9d68@85.221.141.144:30303\"")]
            string enode,
            [JsonRpcParameter(Description = "Removing from static nodes if `true` (optional)", ExampleValue = "true")]
            bool removeFromStaticNodes = false);


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


        [JsonRpcMethod(Description = "Base data directory path",
            IsImplemented = false)]
        ResultWrapper<string> admin_dataDir();


        [JsonRpcMethod(Description = "[DEPRECATED]",
            IsImplemented = false)]
        ResultWrapper<bool> admin_setSolc();

        [JsonRpcMethod(Description = "Runs full pruning if enabled.",
            EdgeCaseHint = "",
            ExampleResponse = "\"Starting\"",
            IsImplemented = true)]
        ResultWrapper<PruningStatus> admin_prune();
    }
}
