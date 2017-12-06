namespace Nevermind.JsonRpc
{
    public interface IJsonRpcService
    {
        string SendRequest(string request);
    }
}