using System;
using System.Net.Mime;
using Nethermind.Core;

namespace Nethermind.Runner.TestClient
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Welcome in Runner Test Client");
            var logger = new ConsoleLogger();
            var client = new RunnerTestCientApp(new RunnerTestCient(logger, new JsonSerializer(logger)));
            client.Start();
            Console.WriteLine("Exiting Runner Test Client");
        }
    }
}
