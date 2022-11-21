// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Jint.Native;
using Nethermind.Cli.Console;
using Nethermind.Cli.Modules;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

[assembly: InternalsVisibleTo("Nethermind.Cli.Test")]
namespace Nethermind.Cli
{
    public static class Program
    {
        private static readonly ColorScheme ColorScheme = DraculaColorScheme.Instance;
        private static readonly CliConsole CliConsole = new CliConsole();
        private static readonly StatementHistoryManager HistoryManager = new StatementHistoryManager(CliConsole);

        private static readonly ILogManager LogManager = new OneLoggerLogManager(new CliLogger(CliConsole));
        private static readonly IJsonSerializer Serializer = new EthereumJsonSerializer();

        private static readonly Terminal Terminal = CliConsole.Init(DraculaColorScheme.Instance);
        private static readonly ICliEngine Engine = new CliEngine(CliConsole);
        private static readonly INodeManager NodeManager = new NodeManager(Engine, Serializer, CliConsole, LogManager);

        private static readonly CliModuleLoader ModuleLoader = new CliModuleLoader(Engine, NodeManager, CliConsole);

        public static void Main()
        {
            RegisterConverters();
            Engine.JintEngine.SetValue("serialize", new Action<JsValue>(v =>
            {
                string text = Serializer.Serialize(v.ToObject(), true);
                CliConsole.WriteGood(text);
            }));

            ModuleLoader.DiscoverAndLoadModules();
            ReadLine.AutoCompletionHandler = new AutoCompletionHandler(ModuleLoader);

            NodeManager.SwitchUri(new Uri("http://localhost:8545"));
            HistoryManager.Init();
            TestConnection();
            CliConsole.WriteLine();
            RunEvalLoop();
        }

        private static void TestConnection()
        {
            CliConsole.WriteLine($"Connecting to {NodeManager.CurrentUri}");
            JsValue result = Engine.Execute("web3.clientVersion");
            if (result != JsValue.Null)
            {
                CliConsole.WriteGood("Connected");
            }
        }

        internal static string RemoveDangerousCharacters(string statement)
        {
            if (string.IsNullOrWhiteSpace(statement))
            {
                return "";
            }

            StringBuilder cleaned = new();
            for (int i = 0; i < statement.Length; i++)
            {
                switch (statement[i])
                {
                    case '\x0008':
                        if (cleaned.Length != 0)
                        {
                            cleaned.Remove(cleaned.Length - 1, 1);
                        }
                        break;
                    case '\x0000':
                        return cleaned.ToString();
                    default:
                        cleaned.Append(statement[i]);
                        break;
                }
            }

            return cleaned.ToString();
        }

        private static void RunEvalLoop()
        {
            while (true)
            {
                try
                {
                    if (Terminal != Terminal.Cmder)
                    {
                        Colorful.Console.ForegroundColor = ColorScheme.Text;
                    }

                    const int bufferSize = 1024 * 16;
                    string statement;
                    using (Stream inStream = System.Console.OpenStandardInput(bufferSize))
                    {
                        Colorful.Console.SetIn(new StreamReader(inStream, Colorful.Console.InputEncoding, false, bufferSize));
                        CliConsole.WriteLessImportant("nethermind> ");
                        statement = Terminal == Terminal.Cygwin ? Colorful.Console.ReadLine() : ReadLine.Read();
                        statement = RemoveDangerousCharacters(statement);

                        HistoryManager.UpdateHistory(statement);
                    }

                    if (statement == "exit")
                    {
                        break;
                    }

                    JsValue result = Engine.Execute(statement);
                    WriteResult(result);
                }
                catch (Exception e)
                {
                    CliConsole.WriteException(e);
                }
            }
        }

        private static void WriteResult(JsValue result)
        {
            if (result.IsObject() && result.AsObject().Class == "Function")
            {
                CliConsole.WriteGood(result.ToString());
                CliConsole.WriteLine();
            }
            else if (!result.IsNull())
            {
                string text = Serializer.Serialize(result.ToObject(), true);
                CliConsole.WriteGood(text);
            }
            else
            {
                CliConsole.WriteLessImportant("null");
                CliConsole.WriteLine();
            }
        }

        private static void RegisterConverters()
        {
            Serializer.RegisterConverter(new ParityTxTraceFromReplayConverter());
            Serializer.RegisterConverter(new ParityAccountStateChangeConverter());
            Serializer.RegisterConverter(new ParityTraceActionConverter());
            Serializer.RegisterConverter(new ParityTraceResultConverter());
            Serializer.RegisterConverter(new ParityVmOperationTraceConverter());
            Serializer.RegisterConverter(new ParityVmTraceConverter());
            Serializer.RegisterConverter(new TransactionForRpcWithTraceTypesConverter());
        }
    }
}
