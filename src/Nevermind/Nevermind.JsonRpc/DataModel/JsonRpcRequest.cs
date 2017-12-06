namespace Nevermind.JsonRpc.DataModel
{
    public class JsonRpcRequest
    {
        public string Jsonrpc { get; set; }
        public string Method { get; set; }
        public string[] Params { get; set; }
        public string Id { get; set; }
    }
}