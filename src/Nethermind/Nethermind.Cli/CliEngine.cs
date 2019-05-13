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
using Jint.Native;
using Jint.Parser;
using Nethermind.Cli.Modules;
using Nethermind.Core.Extensions;

namespace Nethermind.Cli
{
    public class CliEngine : ICliEngine
    {
        public Engine JintEngine { get; }

        public CliEngine()
        {
            JintEngine = new Engine();
            JintEngine.SetValue("gasPrice", (double) 20.GWei());
        }
        
        public JsValue Execute(string statement)
        {
            try
            {
                return JintEngine.Execute(statement).GetCompletionValue();
            }
            catch (ParserException e)
            {
                CliConsole.WriteErrorLine(e.Message);
            }
            catch (CliArgumentParserException e)
            {
                CliConsole.WriteErrorLine(e.Message);
            }
            catch (Exception e)
            {
                CliConsole.WriteException(e);
            }

            return JsValue.Null;
        }
    }
}