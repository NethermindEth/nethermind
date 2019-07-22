namespace Nethermind.Grpc
{
    public class GrpcConfig : IGrpcConfig
    {
        public bool Enabled { get; set; } = true;
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 50000;
        public bool ProducerEnabled { get; set; } = false;
    }
}