using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualBasic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Network.P2P.Subprotocols.Les.Messages;
using Nethermind.Serialization.Json;

namespace EngineRequestsGenerator;

public static class Program
{
    static void Main(string[] args)
    {
        StringBuilder stringBuilder = new();
        EthereumJsonSerializer serializer = new();
        ITimestamper timestamper = new IncrementalTimestamper();

        Hash256 genesisHash = new("0x9cbea0de83b440f4462c8280a4b0b4590cdb452069757e2c510cb3456b6c98cc");

        BlockHeader parentHeader = GetParentHeader();
        Block block = new Block(parentHeader);
        ExecutionPayload payload = new ExecutionPayload(block);
        // JsonRpcRequest request1 = GetJsonRpcRequest("method");

        string executionPayloadString = serializer.Serialize(payload);
        string blobsString = serializer.Serialize(Array.Empty<byte[]>());
        string parentBeaconBlockRootString = TestItem.KeccakA.ToString();

        WriteJsonRpcRequest(stringBuilder, nameof(IEngineRpcModule.engine_newPayloadV3), new []{executionPayloadString, blobsString, parentBeaconBlockRootString});
        // JsonObject executionPayloadAsJObject = serializer.Deserialize<JsonObject>(executionPayloadString);
        // JsonRpcRequest request = RpcTest.GetJsonRequest(nameof(IEngineRpcModule.engine_newPayloadV3),
        //         serializer.Serialize(executionPayloadAsJObject), blobsString, parentBeaconBlockRootString);

        // string jsonString = serializer.Serialize(request1);
        File.WriteAllText("requests.txt", stringBuilder.ToString());
    }

    private static BlockHeader GetParentHeader()
    {
        throw new NotImplementedException();
    }

    private static void WriteJsonRpcRequest(StringBuilder stringBuilder, string methodName, string[]? parameters)
    {
        stringBuilder.Append($"{{\"jsonrpc\":\"2.0\",\"method\":\"{methodName}\",");
        if (parameters is not null) stringBuilder.Append($"\"params\":{parameters},");
        stringBuilder.Append("\"id\":67}");
    }



    private static string jsonBeginning = "{\"jsonrpc\":\"2.0\",\"method\":\"";
    private static string jsonEnding = "";

    // private static BlockHeader GetHeader()
    // {
    //     BlockHeader blockHeader = new();
    //
    //     return blockHeader;
    // }
}
