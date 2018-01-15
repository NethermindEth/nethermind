namespace Nevermind.JsonRpc.DataModel
{
    public interface IJsonRpcRequest
    {
        void FromJson(string jsonValue);
    }
}