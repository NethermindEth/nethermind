using System;
using System.Threading.Tasks;
using Nevermind.Network.P2P;
using Nevermind.Network.Rlpx;

namespace Nevermind.PeerConsole
{
    internal static class Program
    {
        public static async Task Main(string[] args)
        {   
            Console.WriteLine("Initializing server...");
            RlpxPeer peerServerA = new RlpxPeer();
            RlpxPeer peerServerB = new RlpxPeer();
            await Task.WhenAll(peerServerA.Init(30304), peerServerB.Init(30305));
            Console.WriteLine("Servers running...");
            Console.ReadLine();
            Console.WriteLine("Shutting down...");
            await Task.WhenAll(peerServerA.Shutdown(), peerServerB.Shutdown());
            Console.WriteLine("Goodbye...");
        }
    }
}