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
using System.Drawing;
using System.IO;
using System.Linq;
using Jint.Native;
using Nethermind.Cli.Modules;
using Nethermind.Core;
using Nethermind.Core.Json;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
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

        // ReSharper disable once InconsistentNaming
        private static CliModuleLoader ModuleLoader;

        private const string HistoryFilePath = "cli.cmd.history";
        private static readonly ColorScheme ColorScheme = new DraculaColorScheme();

        private static void CurrentDomainOnProcessExit(object sender, EventArgs e)
        {
            File.WriteAllLines(HistoryFilePath, ReadLine.GetHistory().TakeLast(60));
        }

        
        static void Main(string[] args)
        {
            _terminal = CliConsole.Init(ColorScheme);

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

        private static string CleanStatement(string statement)
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
            try
            {
                if (File.Exists(HistoryFilePath))
                {
                    foreach (string line in File.ReadLines(HistoryFilePath).TakeLast(60))
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
                CliConsole.WriteErrorLine($"Could not load cmd history from {HistoryFilePath} {e.Message}");
            }

            ReadLine.AutoCompletionHandler = new AutoCompletionHandler();

            while (true)
            {
                try
                {
                    if (_terminal != Terminal.Cmder)
                    {
                        Console.ForegroundColor = ColorScheme.Text;
                    }
                    int bufferSize = 1024 * 16;
                    string statement;
                    using (Stream inStream = System.Console.OpenStandardInput(bufferSize))
                    {
                        Console.SetIn(new StreamReader(inStream, Console.InputEncoding, false, bufferSize));
                        CliConsole.WriteLessImportant("nethermind> ");
                        statement = _terminal == Terminal.Cygwin ? Console.ReadLine() : ReadLine.Read();
                        CleanStatement(statement);
                        
                        if (!File.Exists(HistoryFilePath))
                        {
                            File.Create(HistoryFilePath).Dispose();
                        }
                        
                        if (!SecuredCommands.Any(sc => statement.Contains(sc)))
                        {
                            ReadLine.AddHistory(statement);
                            
                            using (var fileStream = File.AppendText(HistoryFilePath))
                            {
                                fileStream.WriteLine(statement);
                            }
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
                    if (result.IsObject() && result.AsObject().Class == "Function")
                    {
                        CliConsole.WriteGood(result.ToString());
                        CliConsole.WriteLine();
                    }
                    else if (!result.IsNull())
                    {
                        string text = Serializer.Serialize(result.ToObject(), true);
//                        File.AppendAllText("C:\\temp\\cli.txt", text);
                        CliConsole.WriteGood(text);
                    }
                    else
                    {
                        CliConsole.WriteLessImportant("null");
                        CliConsole.WriteLine();
                    }

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

        private class CliLogger : ILogger
        {
            public void Info(string text)
            {
                throw new NotImplementedException();
            }

            public void Warn(string text)
            {
                CliConsole.WriteLessImportant(text);
            }

            public void Debug(string text)
            {
                throw new NotImplementedException();
            }

            public void Trace(string text)
            {
                throw new NotImplementedException();
            }

            public void Error(string text, Exception ex = null)
            {
                CliConsole.WriteErrorLine(text);
                if (ex != null)
                {
                    CliConsole.WriteException(ex);
                }
            }

            public bool IsInfo => false;
            public bool IsWarn => true;
            public bool IsDebug => false;
            public bool IsTrace => false;
            public bool IsError => true;
        }

        private static void Setup()
        {
            Serializer.RegisterConverter(new ParityLikeTxTraceConverter());
            Serializer.RegisterConverter(new ParityAccountStateChangeConverter());
            Serializer.RegisterConverter(new ParityTraceActionConverter());
            Serializer.RegisterConverter(new ParityTraceResultConverter());
            Serializer.RegisterConverter(new ParityVmOperationTraceConverter());
            Serializer.RegisterConverter(new ParityVmTraceConverter());

            _engine = new CliEngine();
            _engine.JintEngine.SetValue("serialize", new Action<JsValue>(v =>
            {
                string text = Serializer.Serialize(v.ToObject(), true);
//                File.AppendAllText("C:\\temp\\cli.txt", text);
                CliConsole.WriteGood(text);
            }));
            
            _logManager = new OneLoggerLogManager(new CliLogger());
            _nodeManager = new NodeManager(_engine, Serializer, _logManager);
            _nodeManager.SwitchUri(new Uri("http://localhost:8545"));
        }

        private static void LoadModules()
        {
            ModuleLoader = new CliModuleLoader(_engine, _nodeManager);
            ModuleLoader.LoadModule(typeof(AdminCliModule));
            ModuleLoader.LoadModule(typeof(CliqueCliModule));
            ModuleLoader.LoadModule(typeof(DebugCliModule));
            ModuleLoader.LoadModule(typeof(EthCliModule));
            ModuleLoader.LoadModule(typeof(NetCliModule));
            ModuleLoader.LoadModule(typeof(NodeCliModule));
            ModuleLoader.LoadModule(typeof(NdmCliModule));
            ModuleLoader.LoadModule(typeof(TraceCliModule));
            ModuleLoader.LoadModule(typeof(ParityCliModule));
            ModuleLoader.LoadModule(typeof(PersonalCliModule));
            ModuleLoader.LoadModule(typeof(SystemCliModule));
            ModuleLoader.LoadModule(typeof(Web3CliModule));
        }
    }
}