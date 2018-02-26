using System.Threading.Tasks;
using Nevermind.Network.P2P;

namespace Nevermind.Console.Peer
{
    internal static class Program
    {
        public static async Task Main(string[] args)
        {
            System.Console.WriteLine("Initializing server...");
            P2PServer peerServer = new P2PServer();
            await peerServer.Init(30303);
            System.Console.WriteLine("Server running...");
            System.Console.ReadLine();
            System.Console.WriteLine("Shutting down...");
            await peerServer.Shutdown();
            System.Console.WriteLine("Goodbye...");
        }
    }
}