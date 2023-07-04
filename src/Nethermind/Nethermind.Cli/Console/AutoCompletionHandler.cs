// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        public char[] Separators { get; set; } = { ' ', '.', '/' };

        // text - The current text entered in the console
        // index - The index of the terminal cursor within {text}
        public string[] GetSuggestions(string text, int index)
        {
            string[] suggestions = Array.Empty<string>();
            if (!text.Contains('.'))
            {
                suggestions = _cliModuleLoader.ModuleNames.OrderBy(x => x).Where(x => x.StartsWith(text)).ToArray();
            }

            foreach (string moduleName in _cliModuleLoader.ModuleNames)
            {
                if (text.StartsWith($"{moduleName}."))
                {
                    string methodPart = text[(text.IndexOf('.') + 1)..];
                    suggestions = _cliModuleLoader.MethodsByModules[moduleName].Where(x => x.StartsWith(methodPart)).OrderBy(x => x).ToArray();
                    break;
                }
            }

            return suggestions;
        }
    }
}
