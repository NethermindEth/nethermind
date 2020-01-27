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
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Console = Colorful.Console;

namespace Nethermind.Cli
{
    public class CliConsole : ICliConsole
    {
        private static ColorScheme _colorScheme;
        private static Terminal _terminal;
        private static readonly Dictionary<string, Terminal> Terminals = new Dictionary<string, Terminal>
        {
            ["cmd.exe"] = Terminal.Cmd,
            ["cmd"] = Terminal.Cmder,
            ["powershell"] = Terminal.Powershell,
            ["cygwin"] = Terminal.Cygwin
        };

        public Terminal Init(ColorScheme colorScheme)
        {
            _colorScheme = colorScheme;
            Console.BackgroundColor = colorScheme.BackgroundColor;
            Console.ForegroundColor = colorScheme.Text;
            _terminal = GetTerminal();
            if (_terminal != Terminal.Powershell)
            {
                Console.ResetColor();
            }

            if (_terminal != Terminal.Cygwin)
            {
                Console.Clear();
            }

            Assembly assembly = typeof(CliConsole).Assembly;
            AssemblyInformationalVersionAttribute versionAttribute = assembly.GetCustomAttributes(false).OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault();
            string version = versionAttribute?.InformationalVersion;

            Console.WriteLine("**********************************************", _colorScheme.Comment);
            Console.WriteLine();
            Console.WriteLine("Nethermind CLI {0}", GetColor(_colorScheme.Good), version);
            Console.WriteLine("  https://github.com/NethermindEth/nethermind", GetColor(_colorScheme.Interesting));
            Console.WriteLine("  https://nethermind.readthedocs.io/en/latest/", GetColor( _colorScheme.Interesting));
            Console.WriteLine();
            Console.WriteLine("powered by:", _colorScheme.Text);
            Console.WriteLine("  https://github.com/sebastienros/jint", GetColor( _colorScheme.Interesting));
            Console.WriteLine("  https://github.com/tomakita/Colorful.Console", GetColor( _colorScheme.Interesting));
            Console.WriteLine("  https://github.com/tonerdo/readline", GetColor( _colorScheme.Interesting));
            Console.WriteLine();
            Console.WriteLine("**********************************************", _colorScheme.Comment);
            Console.WriteLine();

            return _terminal;
        }

        private Terminal GetTerminal()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? Terminal.LinuxBash :
                    RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Terminal.MacBash : Terminal.Unknown;
            }
            
            var title = Console.Title.ToLowerInvariant();
            foreach (var (key, value) in Terminals)
            {
                if (title.Contains(key))
                {
                    return value;
                }
            }

            return Terminal.Unknown;
        }

        private Color GetColor(Color defaultColor)
        {
            return _terminal == Terminal.LinuxBash ? _colorScheme.Text : defaultColor;
        }
        
        public void WriteException(Exception e)
        {
            Console.WriteLine(e.ToString(), GetColor(_colorScheme.ErrorColor));
        }
        
        public void WriteErrorLine(string errorMessage)
        {
            Console.WriteLine(errorMessage, GetColor(_colorScheme.ErrorColor));
        }
        
        public void WriteLine(object objectToWrite)
        {
            Console.WriteLine(objectToWrite.ToString(), _colorScheme.Text);
        }
        
        public void WriteCommentLine(object objectToWrite)
        {
            Console.WriteLine(objectToWrite.ToString(), _colorScheme.Comment);
        }
        
        public void WriteLessImportant(object objectToWrite)
        {
            Console.Write(objectToWrite.ToString(), GetColor(_colorScheme.LessImportant));
        }
        
        public void WriteKeyword(string keyword)
        {
            Console.Write(keyword, GetColor(_colorScheme.Keyword));
        }
        
        public void WriteInteresting(string interesting)
        {
            Console.Write(interesting, GetColor(_colorScheme.Interesting));
        }

        public void WriteLine()
        {
            Console.WriteLine();
        }

        public void WriteGood(string goodText)
        {
            Console.WriteLine(goodText, GetColor(_colorScheme.Good));
        }

        public void WriteString(object result)
        {
            Console.WriteLine(result, GetColor(_colorScheme.String));
        }
    }
}