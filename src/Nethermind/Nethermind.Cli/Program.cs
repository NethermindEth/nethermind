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
using Jint;
using Nethermind.Core;
using Nethermind.Core.Json;
using Nethermind.Core.Logging;
using Nethermind.JsonRpc.Client;

namespace Nethermind.Cli
{
    static class Program
    {
        private static IJsonSerializer _serializer = new EthereumJsonSerializer();
        
        static void Main(string[] args)
        {
            ILogManager logManager = new OneLoggerLogManager(new ConsoleAsyncLogger(LogLevel.Debug));
            BasicJsonRpcClient client = new BasicJsonRpcClient(new Uri("http://localhost:8545"), _serializer, logManager);

            var engine = new Engine();
            BuildPersonal(engine, client);

            while (true)
            {
                try
                {
                    Console.Write("> ");
                    var statement = Console.ReadLine();
                    if (statement == "exit")
                    {
                        break;
                    }

                    Console.WriteLine(engine.Execute(statement).GetCompletionValue());
                }
                catch (Exception e)
                {
                    var color = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(e);
                    Console.ForegroundColor = color;
                }
            }
        }

        private static void BuildPersonal(Engine engine, BasicJsonRpcClient client)
        {
            new CliApiBuilder(engine, "personal")
                .AddProperty("listAccounts", () => _serializer.Serialize(client.Post<Address[]>("personal_listAccounts").Result))
                .AddMethod("newAccount", (string passphrase) => _serializer.Serialize(client.Post<Address>("personal_newAccount", passphrase).Result.ToString()))
                .Build();
        }
    }
}