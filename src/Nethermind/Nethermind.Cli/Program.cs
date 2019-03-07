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
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Jint;
using Jint.Native;
using Nethermind.Cli.Modules;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Cli
{   
    static class Program
    {
        private static IJsonSerializer _serializer = new EthereumJsonSerializer();
        private static INodeManager _nodeManager;
        private static ILogManager _logManager;
        private static ICliEngine _engine;

        // ReSharper disable once InconsistentNaming
        private static CliModuleLoader ModuleLoader;

        static void Main(string[] args)
        {
            Setup();
            LoadModules();
            RunEvalLoop();
        }

        private static void RunEvalLoop()
        {
            while (true)
            {
                try
                {
                    Console.Write("> ");
                    
                    int bufferSize = 1024 * 16;
                    string statement;
                    using (Stream inStream = Console.OpenStandardInput(bufferSize))
                    {
                        Console.SetIn(new StreamReader(inStream, Console.InputEncoding, false, bufferSize));
                        statement = Console.ReadLine();
                    }

                    if (statement == "exit")
                    {
                        break;
                    }

                    JsValue result = _engine.Execute(statement);
                    bool isNull = result.IsNull();
                    Console.WriteLine(isNull ? "null" : result);
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

        private static void Setup()
        {
            _logManager = new OneLoggerLogManager(new ConsoleAsyncLogger(LogLevel.Debug));
            _nodeManager = new NodeManager(_serializer, _logManager);
            _nodeManager.SwitchUri(new Uri("http://localhost:8545"));
            _engine = new CliEngine();
        }

        private static void LoadModules()
        {
            ModuleLoader = new CliModuleLoader(_engine, _nodeManager);
            ModuleLoader.LoadModule(typeof(PersonalCliModule));
            ModuleLoader.LoadModule(typeof(EthCliModule));
            ModuleLoader.LoadModule(typeof(NetCliModule));
            ModuleLoader.LoadModule(typeof(Web3CliModule));
            ModuleLoader.LoadModule(typeof(NodeCliModule));
            ModuleLoader.LoadModule(typeof(CliqueCliModule));
            ModuleLoader.LoadModule(typeof(DebugCliModule));
        }
    }
}