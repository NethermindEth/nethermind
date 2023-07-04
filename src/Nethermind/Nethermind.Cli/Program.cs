// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using System.Text;
using Jint.Native;
using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Cli.Console;
using Nethermind.Cli.Modules;
using Nethermind.Config;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

[assembly: InternalsVisibleTo("Nethermind.Cli.Test")]
namespace Nethermind.Cli
{
    public static class Program
    {
        private static readonly Dictionary<string, ColorScheme> _availableColorSchemes = new(){  {"basic", BasicColorScheme.Instance },
                                                                                                {"dracula", DraculaColorScheme.Instance }};
        private static readonly IJsonSerializer Serializer = new EthereumJsonSerializer();

        public static void Main(string[] args)
        {
            CommandLineApplication app = new() { Name = "Nethermind.Cli" };
            _ = app.HelpOption("-?|-h|--help");

            var colorSchemeOption = app.Option("-cs|--colorScheme <colorScheme>", "Color Scheme. Possible values: Basic|Dracula", CommandOptionType.SingleValue);
            var nodeAddressOption = app.Option("-a|--address <address>", "Node Address", CommandOptionType.SingleValue);

            app.OnExecute(() =>
            {
                ColorScheme? cs;
                ICliConsole cliConsole = colorSchemeOption.HasValue() && (cs = MapColorScheme(colorSchemeOption.Value())) != null
                    ? new ColorfulCliConsole(cs)
                    : new CliConsole();

                var historyManager = new StatementHistoryManager(cliConsole, new FileSystem());
                ILogManager logManager = new OneLoggerLogManager(new CliLogger(cliConsole));
                ICliEngine engine = new CliEngine(cliConsole);
                INodeManager nodeManager = new NodeManager(engine, Serializer, cliConsole, logManager);
                var moduleLoader = new CliModuleLoader(engine, nodeManager, cliConsole);

                RegisterConverters();
                engine.JintEngine.SetValue("serialize", new Action<JsValue>(v =>
                {
                    string text = Serializer.Serialize(v.ToObject(), true);
                    cliConsole.WriteGood(text);
                }));

                moduleLoader.DiscoverAndLoadModules();
                ReadLine.AutoCompletionHandler = new AutoCompletionHandler(moduleLoader);

                string nodeAddress = nodeAddressOption.HasValue()
                    ? nodeAddressOption.Value()
                    : "http://localhost:8545";
                nodeManager.SwitchUri(new Uri(nodeAddress));
                historyManager.Init();
                TestConnection(nodeManager, engine, cliConsole);
                cliConsole.WriteLine();
                RunEvalLoop(engine, historyManager, cliConsole);

                cliConsole.ResetColor();
                return ExitCodes.Ok;
            });

            try
            {
                app.Execute(args);
            }
            catch (CommandParsingException)
            {
                app.ShowHelp();
            }
        }

        private static void TestConnection(INodeManager nodeManager, ICliEngine cliEngine, ICliConsole cliConsole)
        {
            cliConsole.WriteLine($"Connecting to {nodeManager.CurrentUri}");
            JsValue result = cliEngine.Execute("web3.clientVersion");
            if (result != JsValue.Null)
            {
                cliConsole.WriteGood("Connected");
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

        private static void RunEvalLoop(ICliEngine engine, StatementHistoryManager historyManager, ICliConsole console)
        {
            while (true)
            {
                try
                {
                    const int bufferSize = 1024 * 16;
                    string statement;
                    using (Stream inStream = System.Console.OpenStandardInput(bufferSize))
                    {
                        Colorful.Console.SetIn(new StreamReader(inStream, Colorful.Console.InputEncoding, false, bufferSize));
                        console.WriteLessImportant("nethermind> ");
                        statement = console.Terminal == Terminal.Cygwin ? Colorful.Console.ReadLine() : ReadLine.Read();
                        statement = RemoveDangerousCharacters(statement);

                        historyManager.UpdateHistory(statement);
                    }

                    if (statement == "exit")
                    {
                        break;
                    }

                    JsValue result = engine.Execute(statement);
                    WriteResult(console, result);
                }
                catch (Exception e)
                {
                    console.WriteException(e);
                }
            }
        }

        private static void WriteResult(ICliConsole cliConsole, JsValue result)
        {
            if (result.IsObject() && result.AsObject().Class == "Function")
            {
                cliConsole.WriteGood(result.ToString());
                cliConsole.WriteLine();
            }
            else if (!result.IsNull())
            {
                string text = Serializer.Serialize(result.ToObject(), true);
                cliConsole.WriteGood(text);
            }
            else
            {
                cliConsole.WriteLessImportant("null");
                cliConsole.WriteLine();
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

        private static ColorScheme? MapColorScheme(string colorSchemeOption)
        {
            return _availableColorSchemes.TryGetValue(colorSchemeOption.ToLower(), out var scheme) ? scheme : null;
        }
    }
}
