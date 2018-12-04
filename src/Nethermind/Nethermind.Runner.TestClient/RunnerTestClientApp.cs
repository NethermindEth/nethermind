/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Client;

namespace Nethermind.Runner.TestClient
{
    public class RunnerTestClientApp : IRunnerTestCientApp
    {
        private readonly IJsonRpcClient _client;

        public RunnerTestClientApp(IJsonRpcClient client)
        {
            _client = client;
        }

        public async Task Run()
        {
            Option[] options =
            {
                new Option("eth_protocolVersion"),
                new Option("eth_getBlockByNumber", ("Block Number", ToBlockNumberHex), ("Full Transaction Data?", ToBool)),
                new Option("eth_getTransactionReceipt", ("Tx Hash", p => p)),
                new Option("eth_accounts"),
                new Option("debug_traceTransaction", ("Tx Hash", p => p)),
                new Option("debug_traceTransactionByBlockAndIndex", ("Block Number", ToBlockNumberHex), ("Tx Index", s => int.Parse(s))),
                new Option("debug_traceTransactionByBlockhashAndIndex", ("Block Hash", p => p), ("Tx Index", s => int.Parse(s))),
                new Option("debug_traceBlockByHash", ("Block Hash", p => p)),
                new Option("debug_traceBlockByNumber", ("Block Number", ToBlockNumberHex)),
                new Option("debug_addTxData", ("Block Number", ToBlockNumberHex)),
                new Option("debug_getFromDb", ("DB Name", p => p), ("Bytes", p => p)),
                new Option("clique_getSigners"),
                new Option("clique_getSignersAtHash", ("Block Hash", p => p)),
                new Option("clique_propose", ("Signer", p => p), ("Vote", ToBool)),
                new Option("clique_discard", ("Signer", p => p)),
                new Option("trace_replayTransaction", ("Hash", p => p), ("Type", p => $"[\"{p}\"]")),
                new Option("trace_replayBlockTransactions", ("Block Number", ToBlockNumberHex), ("Type", p => $"[\"{p}\"]")),
                new Option("net_dumpPeerConnectionDetails"),
            };

            StringBuilder prompt = new StringBuilder("Options:");
            for (int i = 0; i < options.Length; i++) prompt.Append($"{i} - {options[i].Command}, ");

            prompt.AppendLine("e - exit");
            prompt.AppendLine("Enter command:");

            while (true)
            {
                Console.Write(prompt);
                string action = Console.ReadLine();
                if (action.CompareIgnoreCaseTrim("e")) return;

                if (int.TryParse(action, out int chosenCommand) && options.Length > chosenCommand)
                {
                    Option option = options[chosenCommand];
                    var parameters = new object[option.Parameters.Length];
                    for (int i = 0; i < option.Parameters.Length; i++)
                    {
                        Console.Write($"{option.Parameters[i].Name}: ");
                        parameters[i] = option.Parameters[i].Parse(Console.ReadLine());
                    }

                    string result = await _client.Post(option.Command, parameters);
                    PrintResult(result);
                }
                else
                {
                    Console.WriteLine("Incorrect command");
                }
            }
        }

        private object ToBlockNumberHex(string number)
        {
            return string.Format("0x{0:x}", int.Parse(number));
        }
        
        private object ToBool(string yesNo)
        {
            return
                yesNo == "y" ||
                yesNo == "1" ||
                yesNo == "t" ||
                yesNo == "Y" ||
                yesNo == "T" ||
                yesNo == "yes" ||
                yesNo == "YES" ||
                yesNo == "Yes" ||
                yesNo == "True" ||
                yesNo == "true" ||
                yesNo == "TRUE";
        }

        private void PrintResult(string result)
        {
            Console.WriteLine("Response:");
            Console.WriteLine(result);
        }

        public class Option
        {
            public Option(string command, params (string, Func<string, object>)[] parameters)
            {
                Command = command;
                Parameters = parameters;
            }

            public string Command { get; set; }
            public (string Name, Func<string, object> Parse)[] Parameters { get; }
        }
    }
}