//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Evm;

namespace Nethermind.EvmPlayground
{
    public static class Program
    {
        private static Client _client = new Client();

        public static async Task Main(string[] args)
        {
            if (args.Length > 0)
            {
                string codeText = File.ReadAllText(args[0]);
                codeText = codeText.TrimEnd(codeText[codeText.Length - 1]);
                await ExecuteCode(codeText);
            }
            else
            {
                while (true)
                {
                    await Execute();
                }    
            }
        }

        private static async Task Execute()
        {
            try
            {
                Console.WriteLine();
                Console.WriteLine("======================================================");
                Console.WriteLine("Enter code and press [ENTER]");
                string codeText = ReadLine.Read("bytecode> ");
                ReadLine.AddHistory(codeText);
                await ExecuteCode(codeText);
            }
            catch (Exception e)
            {
                WriteError(e.Message);
            }
        }

        private static async Task ExecuteCode(string codeText)
        {
            codeText = RunMacros(codeText);
            Console.WriteLine(codeText);
            Console.WriteLine(codeText.Replace(" 0x", string.Empty));
            var code = codeText.Split(" ", StringSplitOptions.RemoveEmptyEntries).Select(b => byte.Parse(b.Replace("0x", string.Empty), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            string hash = await _client.SendInit(code);
            await Task.Delay(100);
            string receipt = await _client.GetReceipt(hash);
            if (receipt.StartsWith("Error:"))
            {
                WriteError(receipt);
            }
            else
            {
                Console.WriteLine(receipt);
                string trace = await _client.GetTrace(hash);
                Console.WriteLine(trace);    
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
        
        private static string RemoveBadCharacters(string input)
        {
            List<char> result = new List<char>();
            int skip = 0;
            foreach (char c in input)
            {
                if (skip > 0)
                {
                    skip--;
                    continue;
                }
                
                if (c == 27)
                {
                    skip = 2;
                }
                else
                {
                    result.Add(c);
                }
            }

            string output = new string(result.ToArray());
            return output.Trim();
        }

        private static string RunMacros(string input)
        {
            input = RemoveBadCharacters(input);
            input = input.Replace("PZ1", "96 0");
            input = ExpandPushHex(input);
            input = Instructions(input);
            input = MakeHex(input);
            return input;
        }
    }
}