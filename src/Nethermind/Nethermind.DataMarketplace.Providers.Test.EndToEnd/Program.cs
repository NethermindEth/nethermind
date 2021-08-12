using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.DataMarketplace.Providers.Test.EndToEnd
{
    class Program
    {
        public static async Task Main(string[] args)
        {
            Console.WriteLine(Figgle.FiggleFonts.Doom.Render("NDM Provider E2E"));
            var jsonRpcUrl = GetValue("JSON_RPC_URL", args?.FirstOrDefault(), "http://localhost:8545");
            var inputDisabled = GetValue("INPUT_DISABLED", args?.Skip(1).FirstOrDefault(), "false") is "true";
            Console.WriteLine($"JSON RPC URL: {jsonRpcUrl}");

            if (!inputDisabled)
            {
                Console.WriteLine("Press any key to start the scenario.");
                Console.ReadKey();
            }

            try
            {
                var scenario = new Scenario(jsonRpcUrl);
                await scenario.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            
            if (!inputDisabled)
            {
                Console.WriteLine("Press any key to quit.");
                Console.ReadKey();
            }
        }

        private static string GetValue(string env, string arg, string @default)
        {
            var value = Environment.GetEnvironmentVariable(env);
            if (string.IsNullOrWhiteSpace(value))
            {
                value = arg;
            }

            return string.IsNullOrWhiteSpace(value) ? @default : value;
        }
    }
}