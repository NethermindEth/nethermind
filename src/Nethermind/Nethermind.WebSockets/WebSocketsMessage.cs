namespace Nethermind.WebSockets
{
    public class WebSocketsMessage
    {
        public string Type { get; }
        public string Client { get; }
        public object Data { get; }

        public WebSocketsMessage(string type, string client, object data)
        {
            Type = type;
            Client = client;
            Data = data;
        }
    }
}