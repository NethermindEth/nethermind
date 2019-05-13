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
using System.Linq;
using Jint.Native;
using Nethermind.Cli.Modules;
using Nethermind.Core;
using Nethermind.Core.Json;
using Nethermind.Core.Logging;
using Nethermind.JsonRpc.Modules.Trace;
using Console = Colorful.Console;

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

        private const string _historyFilePath = "cli.cmd.history";

        private static void CurrentDomainOnProcessExit(object sender, EventArgs e)
        {
            File.WriteAllLines(_historyFilePath, ReadLine.GetHistory().TakeLast(60));
        }

        private static ColorScheme _colorScheme = new DraculaColorScheme();
        
        static void Main(string[] args)
        {
            CliConsole.Init(_colorScheme);

            AppDomain.CurrentDomain.ProcessExit += CurrentDomainOnProcessExit;

            Setup();
            LoadModules();
            Test();

            RunEvalLoop();
        }

        private static void Test()
        {
            CliConsole.WriteLine($"Connecting to {_nodeManager.CurrentUri}");
            JsValue result = _engine.Execute("web3.clientVersion");
            if (result != JsValue.Null)
            {
                CliConsole.WriteGood("Connected");
            }
//            Console.WriteLine(_serializer.Serialize(result.ToObject(), true));
            CliConsole.WriteLine();
        }

        class AutoCompletionHandler : IAutoCompleteHandler
        {
            // characters to start completion from
            public char[] Separators { get; set; } = new char[] {' ', '.', '/'};

            // text - The current text entered in the console
            // index - The index of the terminal cursor within {text}
            public string[] GetSuggestions(string text, int index)
            {
                if (text.IndexOf('.') == -1)
                {
                    return ModuleLoader.ModuleNames.OrderBy(x => x).Where(x => x.StartsWith(text)).ToArray();
                }

                foreach (string moduleName in ModuleLoader.ModuleNames)
                {
                    if (text.StartsWith($"{moduleName}."))
                    {
                        string methodPart = text.Substring(text.IndexOf('.') + 1);
                        return ModuleLoader.MethodsByModules[moduleName].Where(x => x.StartsWith(methodPart)).OrderBy(x => x).ToArray();
                    }
                }

                return null;
            }
        }

        private static IEnumerable<string> SecuredCommands
        {
            get
            {
                yield return "unlockAccount";
                yield return "newAccount";
            }
        }

        private const string _removedString = "*removed*";

        private static void RunEvalLoop()
        {
            try
            {
                if (File.Exists(_historyFilePath))
                {
                    foreach (string line in File.ReadLines(_historyFilePath).TakeLast(60))
                    {
                        if (line != _removedString)
                        {
                            ReadLine.AddHistory(line);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                CliConsole.WriteErrorLine($"Could not load cmd history from {_historyFilePath} {e.Message}");
            }

            ReadLine.AutoCompletionHandler = new AutoCompletionHandler();

            while (true)
            {
                try
                {
                    int bufferSize = 1024 * 16;
                    string statement;
                    using (Stream inStream = System.Console.OpenStandardInput(bufferSize))
                    {
                        Console.SetIn(new StreamReader(inStream, Console.InputEncoding, false, bufferSize));
                        CliConsole.WriteLessImportant("nethermind> ");
                        statement = ReadLine.Read();
                        if (!SecuredCommands.Any(sc => statement.Contains(sc)))
                        {
                            ReadLine.AddHistory(statement);
                        }
                        else
                        {
                            ReadLine.AddHistory(_removedString);
                        }
                    }

                    if (statement == "exit")
                    {
                        break;
                    }

                    JsValue result = _engine.Execute(statement);
                    CliConsole.WriteGood(_serializer.Serialize(result.ToObject(), true));

//                    bool isNull = result.IsNull();
//                    if (!isNull)
//                    {
//                        CliConsole.WriteString(result);
//                    }
                }
                catch (Exception e)
                {
                    CliConsole.WriteException(e);
                }
            }
        }

        private static void Setup()
        {
            _serializer.RegisterConverter(new ParityLikeTxTraceConverter());
            _serializer.RegisterConverter(new ParityAccountStateChangeConverter());
            _serializer.RegisterConverter(new ParityTraceActionConverter());
            _serializer.RegisterConverter(new ParityTraceResultConverter());

            _logManager = new OneLoggerLogManager(new ConsoleAsyncLogger(LogLevel.Info));
            _nodeManager = new NodeManager(_serializer, _logManager);
            _nodeManager.SwitchUri(new Uri("http://localhost:8545"));
            _engine = new CliEngine();
        }

        private static void LoadModules()
        {
            ModuleLoader = new CliModuleLoader(_engine, _nodeManager);
            ModuleLoader.LoadModule(typeof(CliqueCliModule));
            ModuleLoader.LoadModule(typeof(DebugCliModule));
            ModuleLoader.LoadModule(typeof(EthCliModule));
            ModuleLoader.LoadModule(typeof(NetCliModule));
            ModuleLoader.LoadModule(typeof(NodeCliModule));
            ModuleLoader.LoadModule(typeof(ParityCliModule));
            ModuleLoader.LoadModule(typeof(PersonalCliModule));
            ModuleLoader.LoadModule(typeof(Web3CliModule));
        }
    }
}