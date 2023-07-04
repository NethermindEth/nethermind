// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Parser;
using Jint.Runtime;
using Jint.Runtime.Interop;
using Nethermind.Cli.Console;
using Nethermind.Cli.Converters;
using Nethermind.Cli.Modules;
using Nethermind.Core.Extensions;

namespace Nethermind.Cli
{
    public class CliEngine : ICliEngine
    {
        private readonly ICliConsole _cliConsole;
        public Engine JintEngine { get; }

        public CliEngine(ICliConsole cliConsole)
        {
            _cliConsole = cliConsole;
            JintEngine = new Engine();
            JintEngine.SetValue("gasPrice", (double)20.GWei());
            JintEngine.SetValue("load", new Action<string>(LoadFile));
            JintEngine.SetValue("log", new Action<JsValue>(v =>
            {
                //                File.AppendAllText("C:\\temp\\cli.txt", v.ToString());
                Colorful.Console.WriteLine(v.ToString());
            }));

            JintEngine.Global.FastAddProperty("window", JintEngine.Global, false, false, false);

            ObjectInstance console = JintEngine.Object.Construct(Arguments.Empty);
            JintEngine.SetValue("console", console);
            console.Put("log", new DelegateWrapper(JintEngine, new Action<JsValue>(v =>
            {
                //                File.AppendAllText("C:\\temp\\cli.txt", v.ToString());
                Colorful.Console.WriteLine(v.ToString());
            })), false);

            JintEngine.ClrTypeConverter = new FallbackTypeConverter(JintEngine.ClrTypeConverter, new BigIntegerTypeConverter());
        }

        private void LoadFile(string filePath)
        {
            string content = File.ReadAllText(filePath);
            JintEngine.Execute(content);
        }

        public JsValue Execute(string statement)
        {
            try
            {
                Engine e = JintEngine.Execute(statement);
                return e.GetCompletionValue();
            }
            catch (ParserException e)
            {
                _cliConsole.WriteErrorLine(e.Message);
            }
            catch (CliArgumentParserException e)
            {
                _cliConsole.WriteErrorLine(e.Message);
            }
            catch (Exception e)
            {
                _cliConsole.WriteException(e);
            }

            return JsValue.Null;
        }
    }
}
