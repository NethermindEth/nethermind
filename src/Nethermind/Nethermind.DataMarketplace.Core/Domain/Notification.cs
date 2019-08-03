namespace Nethermind.DataMarketplace.Core.Domain
{
    public class Notification
    {
        public string Type { get; set; }
        public string Client { get; set; }
        public object Data { get; set; }

        public Notification()
        {
        }

        public Notification(string type, object data, string client = null)
        {
            Type = type;
            Client = client ?? string.Empty;
            Data = data;
        }
    }
}