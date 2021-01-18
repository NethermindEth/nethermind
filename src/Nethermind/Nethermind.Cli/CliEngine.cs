//  Copyright (c) 2021 Demerzel Solutions Limited
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
            JintEngine.SetValue("gasPrice", (double) 20.GWei());
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
