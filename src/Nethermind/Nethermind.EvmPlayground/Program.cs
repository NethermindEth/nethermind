using System;
using System.Linq;
using System.Threading.Tasks;
using System.Text;

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

        public static string HexStringToDecString(string hex)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("{0:x2}", hex);
            return sb.ToString();
        }

        private static string ExpandPushHex(string input)
        {
            string[] expansion = input.Split(' ').ToArray();
            string ret;

            for (int i = 0; i < expansion.Length - 1; i++) {
                if (expansion[i] == "PX") {
                    if (expansion[i+1].Length <= 64) {
                        expansion[i] = (96 + (expansion[i+1].Length / 2) - 1).ToString();
                        string decimals = String.Join(" ", Bytes.FromHexString(expansion[i+1]).Select(x => x.ToString()));
                        Console.WriteLine($"PX {expansion[i+1]} expanded to: {expansion[i]} {decimals}");
                        expansion[i+1] = decimals;
                    }
                    else {
                        Console.WriteLine($"PX operand {expansion[i+1]} is greater than 32 bytes");
                    }
                }
            }
            ret = String.Join(" ", expansion);
            return ret;
        }

        private static string RunMacros(string input)
        {
            return ExpandPushHex(input.Replace("PZ1", "96 0"));
        }
    }
}