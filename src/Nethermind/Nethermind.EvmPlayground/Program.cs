using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.EvmPlayground
{
    public static class Program
    {
        public static async Task Main()
        {
            try
            {
                Client client = new Client();
                while (true)
                {
                    Console.WriteLine();
                    Console.WriteLine("======================================================");
                    Console.WriteLine("Enter code and press [ENTER]");
                    string codeText = Console.ReadLine();
                    codeText = RunMacros(codeText);
                    Console.WriteLine(codeText);
                    var code = codeText.Split(" ", StringSplitOptions.RemoveEmptyEntries).Select(Byte.Parse).ToArray();
                    string hash = await client.SendInit(code);
                    await Task.Delay(100);
                    string receipt = await client.GetReceipt(hash);
                    Console.WriteLine(receipt);
                    string trace = await client.GetTrace(hash);
                    Console.WriteLine(trace);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Console.ReadLine();
            }
        }

        private static string RunMacros(string input)
        {
            return input.Replace("PZ1", "96 0");
        }
    }
}