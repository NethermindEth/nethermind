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
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using Jint.Native;
using Jint.Parser.Ast;
using Nethermind.Cli.Console;
using Nethermind.Cli.Modules;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Console = Colorful.Console;

namespace Nethermind.Cli
{
    static class Program
    {
        private static readonly IJsonSerializer Serializer = new EthereumJsonSerializer();
        private static INodeManager _nodeManager;
        private static ILogManager _logManager;
        private static ICliEngine _engine;
        private static Terminal _terminal;
        private static CliConsole _cliConsole;
        private static StatementHistoryManager _historyManager;
        private static ColorScheme _colorScheme;

        // ReSharper disable once InconsistentNaming
        private static CliModuleLoader ModuleLoader;

        static void Main(string[] args)
        {
            _cliConsole = new CliConsole();
            _colorScheme = new DraculaColorScheme();
            _terminal = _cliConsole.Init(_colorScheme);
            _logManager = new OneLoggerLogManager(new CliLogger(_cliConsole));
            _historyManager = new StatementHistoryManager(_cliConsole);

            RegisterConverters();
            InitEngine();
            InitNodeManager();
            LoadCliModules();
            
            ReadLine.AutoCompletionHandler = new AutoCompletionHandler(ModuleLoader);
            _historyManager.Init();
            
            TestConnection();
            _cliConsole.WriteLine();
            
            RunEvalLoop();
        }

        private static void TestConnection()
        {
            _cliConsole.WriteLine($"Connecting to {_nodeManager.CurrentUri}");
            JsValue result = _engine.Execute("web3.clientVersion");
            if (result != JsValue.Null)
            {
                _cliConsole.WriteGood("Connected");
            }
        }

        private static string RemoveDangerousCharacters(string statement)
        {
            List<char> cleaned = new List<char>();
            for (int i = 0; i < statement.Length; i++)
            {
                if (statement[i] == 8)
                {
                    cleaned.RemoveAt(cleaned.Count - 1);
                }
                else
                {
                    cleaned.Add(statement[i]);
                }
            }

            return statement;
        }

        private static void RunEvalLoop()
        {
            while (true)
            {
                try
                {
                    if (_terminal != Terminal.Cmder)
                    {
                        Colorful.Console.ForegroundColor = _colorScheme.Text;
                    }

                    const int bufferSize = 1024 * 16;
                    string statement;
                    using (Stream inStream = System.Console.OpenStandardInput(bufferSize))
                    {
                        Colorful.Console.SetIn(new StreamReader(inStream, Colorful.Console.InputEncoding, false, bufferSize));
                        _cliConsole.WriteLessImportant("nethermind> ");
                        statement = _terminal == Terminal.Cygwin ? Colorful.Console.ReadLine() : ReadLine.Read();
                        statement = RemoveDangerousCharacters(statement);

                        _historyManager.UpdateHistory(statement);
                    }

                    if (statement == "exit")
                    {
                        break;
                    }

                    JsValue result = _engine.Execute(statement);
                    WriteResult(result);
                }
                catch (Exception e)
                {
                    _cliConsole.WriteException(e);
                }
            }
        }

        private static void WriteResult(JsValue result)
        {
            if (result.IsObject() && result.AsObject().Class == "Function")
            {
                _cliConsole.WriteGood(result.ToString());
                _cliConsole.WriteLine();
            }
            else if (!result.IsNull())
            {
                string text = Serializer.Serialize(result.ToObject(), true);
                _cliConsole.WriteGood(text);
            }
            else
            {
                _cliConsole.WriteLessImportant("null");
                _cliConsole.WriteLine();
            }
        }

        private static void InitNodeManager()
        {
            _nodeManager = new NodeManager(_engine, Serializer, _cliConsole, _logManager);
            _nodeManager.SwitchUri(new Uri("http://localhost:8545"));
        }

        private static void InitEngine()
        {
            _engine = new CliEngine(_cliConsole);
            _engine.JintEngine.SetValue("serialize", new Action<JsValue>(v =>
            {
                string text = Serializer.Serialize(v.ToObject(), true);
                _cliConsole.WriteGood(text);
            }));
        }

        private static void RegisterConverters()
        {
            Serializer.RegisterConverter(new ParityTxTraceFromReplayConverter());
            Serializer.RegisterConverter(new ParityAccountStateChangeConverter());
            Serializer.RegisterConverter(new ParityTraceActionConverter());
            Serializer.RegisterConverter(new ParityTraceResultConverter());
            Serializer.RegisterConverter(new ParityVmOperationTraceConverter());
            Serializer.RegisterConverter(new ParityVmTraceConverter());
        }

        private static void LoadCliModules()
        {
            ModuleLoader = new CliModuleLoader(_engine, _nodeManager, _cliConsole);
            var modules = typeof(Program).Assembly.GetTypes().Where(t => t.GetCustomAttribute<CliModuleAttribute>() != null);
            foreach (Type module in modules)
            {
                ModuleLoader.LoadModule(module);
            }
        }
    }
}