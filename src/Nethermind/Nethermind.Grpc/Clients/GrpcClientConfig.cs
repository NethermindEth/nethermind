namespace Nethermind.Grpc.Clients
{
    public class GrpcClientConfig : IGrpcClientConfig
    {
        public bool Enabled { get; set; } = false;
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 50000;
        public string Name { get; set; } = "nethermind";
        public string DisplayName { get; set; } = "Nethermind";
        public string Type { get; set; } = "stream";
        public bool AcceptAllHeaders { get; set; } = false;
        public string AcceptedHeaders { get; set; }
    }
}