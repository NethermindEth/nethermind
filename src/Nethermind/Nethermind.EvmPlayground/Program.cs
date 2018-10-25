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
                    var code = codeText.Split(" ", StringSplitOptions.RemoveEmptyEntries).Select(byte.Parse).ToArray();
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

        private static string ExpandPushHex(string input)
        {
            if (!input.Contains("PX", StringComparison.InvariantCultureIgnoreCase)) return input;

            var expansion = input.Split(' ').ToArray();

            for (int i = 0; i < expansion.Length - 1; i++)
                if (string.Equals(expansion[i], "PX", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (expansion[i + 1].Length <= 64)
                    {
                        expansion[i] = (96 + expansion[i + 1].Length / 2 - 1).ToString();
                        string decimals = string.Join(" ", Bytes.FromHexString(expansion[i + 1]).Select(x => x.ToString()));
                        Console.WriteLine($"PX {expansion[i + 1]} expanded to: {expansion[i]} {decimals}");
                        expansion[i + 1] = decimals;
                    }
                    else
                    {
                        Console.WriteLine($"PX operand {expansion[i + 1]} is greater than 32 bytes");
                    }
                }

            string result = string.Join(' ', expansion);
            return result;
        }

        private static string RunMacros(string input)
        {
            return ExpandPushHex(input.Replace("PZ1", "96 0"));
        }
    }
}