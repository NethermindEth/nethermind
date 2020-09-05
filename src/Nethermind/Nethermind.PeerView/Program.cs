using System;
using Terminal.Gui;

namespace Nethermind.PeerView
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Application.Top.Running = false;
                Application.RequestStop();
                Application.Shutdown();
                Console.WriteLine($"Fatal error.{Environment.NewLine} {e.ExceptionObject}");
            };

            Application.Run<PeersApp>();
        }
    }
}