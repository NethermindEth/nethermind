using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Json;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.EndToEnd.Test
{
    class Program
    {
        public static async Task Main(string[] args)
        {
            Console.WriteLine(Figgle.FiggleFonts.Doom.Render("NDM E2E Scenario"));
            
            var jsonRpcUrl = args?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(jsonRpcUrl))
            {
                jsonRpcUrl = "http://localhost:8545";
            }

            Console.WriteLine($"JSON RPC URL: {jsonRpcUrl}");
            Console.WriteLine("Press any key to start the scenario.");
            Console.ReadKey();

            try
            {
                var scenario = new Scenario(jsonRpcUrl);
                await scenario.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            
            Console.WriteLine("Press any key to quit.");
            Console.ReadKey();
        }
    }
}