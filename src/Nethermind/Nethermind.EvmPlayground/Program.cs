using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Primitives;

namespace Nethermind.EvmPlayground
{
    public static class Program
    {
        public static async Task Main()
        {
            Client client = new Client();
            while (true)
            {
                try
                {
                    Console.WriteLine();
                    Console.WriteLine("======================================================");
                    Console.WriteLine("Enter code and press [ENTER]");
                    string codeText = Console.ReadLine();
                    codeText = RunMacros(codeText);
                    Console.WriteLine(codeText);
                    Console.WriteLine(codeText.Replace(" 0x", string.Empty));
                    var code = codeText.Split(" ", StringSplitOptions.RemoveEmptyEntries).Select(b => byte.Parse(b.Replace("0x", string.Empty), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
                    string hash = await client.SendInit(code);
                    await Task.Delay(100);
                    string receipt = await client.GetReceipt(hash);
                    if (receipt.StartsWith("Error:"))
                    {
                        WriteError(receipt);
                        continue;
                    }

                    Console.WriteLine(receipt);
                    string trace = await client.GetTrace(hash);
                    Console.WriteLine(trace);
                }
                catch (Exception e)
                {
                    WriteError(e.Message);
                }
            }
        }

        private static void WriteError(string message)
        {
            ConsoleColor color = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"{message}");
            Console.ForegroundColor = color;
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

        private static string MakeHex(string input)
        {
            string[] split = input.Split(' ');
            for (int i = 0; i < split.Length; i++)
            {
                if (!split[i].Contains("0x"))
                {
                    int value = int.Parse(split[i]);
                    split[i] = value.ToString("x");
                }

                split[i] = split[i].Replace("0x", "", StringComparison.InvariantCultureIgnoreCase);
                split[i] = split[i].PadLeft(split[i].Length + split[i].Length % 2, '0');
                var newValue = new StringBuilder();
                for (int j = 0; j < split[i].Length; j += 2)
                {
                    if (newValue.Length != 0)
                    {
                        newValue.Append(" ");
                    }

                    newValue.Append(string.Concat("0x", split[i].Substring(j, 2)));
                }

                split[i] = newValue.ToString();
            }

            return string.Join(' ', split);
        }

        private static string RunMacros(string input)
        {
            input = input.Replace("PZ1", "96 0");
            input = ExpandPushHex(input);
            input = MakeHex(input);
            return input;
        }
    }
}