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
using System.Linq;
using Nethermind.Cli.Modules;

namespace Nethermind.Cli.Console
{
    internal class AutoCompletionHandler : IAutoCompleteHandler
    {
        private readonly CliModuleLoader _cliModuleLoader;

        public AutoCompletionHandler(CliModuleLoader cliModuleLoader)
        {
            _cliModuleLoader = cliModuleLoader;
        }
        
        // characters to start completion from
        public char[] Separators { get; set; } = {' ', '.', '/'};

        // text - The current text entered in the console
        // index - The index of the terminal cursor within {text}
        public string[] GetSuggestions(string text, int index)
        {
            string[] suggestions = Array.Empty<string>();
            if (text.IndexOf('.') == -1)
            {
                suggestions = _cliModuleLoader.ModuleNames.OrderBy(x => x).Where(x => x.StartsWith(text)).ToArray();
            }

            foreach (string moduleName in _cliModuleLoader.ModuleNames)
            {
                if (text.StartsWith($"{moduleName}."))
                {
                    string methodPart = text.Substring(text.IndexOf('.') + 1);
                    suggestions = _cliModuleLoader.MethodsByModules[moduleName].Where(x => x.StartsWith(methodPart)).OrderBy(x => x).ToArray();
                    break;
                }
            }

            return suggestions;
        }
    }
}
