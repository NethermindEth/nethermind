using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.IO
using System.Threading.Tasks;
using Microsoft.Extensions.Primitives;
using Nethermind.Evm;

namespace Nethermind.EvmPlayground
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            Client client = new Client();

            if(args.Length > 0)
            {
                try
                {
                    string codeText = File.ReadAllText(args[0];
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
                        return;
                    }

                    Console.WriteLine(receipt);
                    string trace = await client.GetTrace(hash);
                    Console.WriteLine(trace);
                }
                catch (Exception e)
                {
                    WriteError(e.Message);
                }
                return;
            }



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
            if (!input.Contains("PUSHX", StringComparison.InvariantCultureIgnoreCase)) return input;

            var expansion = input.Split(' ').ToArray();

            for (int i = 0; i < expansion.Length - 1; i++)
            {
                if (string.Equals(expansion[i], "PUSHX", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (expansion[i + 1].Length <= 64)
                    {
                        if (expansion[i + 1].StartsWith("0x"))
                        {
                            expansion[i + 1] = expansion[i + 1].Substring(2);
                        }
                        
                        int length = expansion[i + 1].Length;
                        if (length % 2 == 1)
                        {
                            length++;
                            expansion[i + 1] = "0" + expansion[i + 1];
                        }
                        
                        expansion[i] = (96 + length / 2 - 1).ToString();
                        string decimals = string.Join(" ", Bytes.FromHexString(expansion[i + 1]).Select(x => x.ToString()));
                        Console.WriteLine($"PUSHX {expansion[i + 1]} expanded to: {expansion[i]} {decimals}");
                        expansion[i + 1] = decimals;
                    }
                    else
                    {
                        Console.WriteLine($"PUSHX operand {expansion[i + 1]} is greater than 32 bytes");
                    }
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

        private static Dictionary<string, string> _instructions;

        private static string Instructions(string input)
        {
            if (_instructions == null)
            {
                _instructions = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
                foreach (var foo in Enum.GetValues(typeof(Instruction)))
                {
                    _instructions.Add(foo.ToString(), ((byte) foo).ToString());
                }
            }

            string[] split = input.Split(' ');
            for (int i = 0; i < split.Length; i++)
            {
                if (_instructions.ContainsKey(split[i]))
                {
                    split[i] = _instructions[split[i]];
                }
            }

            return string.Join(' ', split);
        }

        private static string RunMacros(string input)
        {
            input = input.Replace("PZ1", "96 0");
            input = ExpandPushHex(input);
            input = Instructions(input);
            input = MakeHex(input);
            return input;
        }
    }
}
