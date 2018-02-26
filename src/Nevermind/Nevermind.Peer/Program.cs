using System;
using System.Threading.Tasks;
using Nevermind.Network.P2P;

namespace Nevermind.Peer
{
    internal static class Program
    {
        public static async Task Main(string[] args)
        {   
            Console.WriteLine("Initializing server...");
            P2PServer peerServerA = new P2PServer();
            P2PServer peerServerB = new P2PServer();
            await Task.WhenAll(peerServerA.Init(30304), peerServerB.Init(30305));
            Console.WriteLine("Servers running...");
            Console.ReadLine();
            Console.WriteLine("Shutting down...");
            await Task.WhenAll(peerServerA.Shutdown(), peerServerB.Shutdown());
            Console.WriteLine("Goodbye...");
        }
    }
}