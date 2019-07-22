namespace Nethermind.WebSockets
{
    public class WebSocketsMessage
    {
        public string Type { get; }
        public object Data { get; }

        public WebSocketsMessage(string type, object data)
        {
            Type = type;
            Data = data;
        }
    }
}