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
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using Nethermind.Core;

namespace Nethermind.Cli.Console
{
    public class CliConsole : ICliConsole
    {
        private static ColorScheme _colorScheme = DraculaColorScheme.Instance;
        
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
            Colorful.Console.BackgroundColor = colorScheme.BackgroundColor;
            Colorful.Console.ForegroundColor = colorScheme.Text;
            _terminal = GetTerminal();
            if (_terminal != Terminal.Powershell)
            {
                Colorful.Console.ResetColor();
            }

            if (_terminal != Terminal.Cygwin)
            {
                Colorful.Console.Clear();
            }
            
            string version = ClientVersion.Version;

            Colorful.Console.WriteLine("**********************************************", _colorScheme.Comment);
            Colorful.Console.WriteLine();
            Colorful.Console.WriteLine("Nethermind CLI {0}", GetColor(_colorScheme.Good), version);
            Colorful.Console.WriteLine("  https://github.com/NethermindEth/nethermind", GetColor(_colorScheme.Interesting));
            Colorful.Console.WriteLine("  https://nethermind.readthedocs.io/en/latest/", GetColor( _colorScheme.Interesting));
            Colorful.Console.WriteLine();
            Colorful.Console.WriteLine("powered by:", _colorScheme.Text);
            Colorful.Console.WriteLine("  https://github.com/sebastienros/jint", GetColor( _colorScheme.Interesting));
            Colorful.Console.WriteLine("  https://github.com/tomakita/Colorful.Console", GetColor( _colorScheme.Interesting));
            Colorful.Console.WriteLine("  https://github.com/tonerdo/readline", GetColor( _colorScheme.Interesting));
            Colorful.Console.WriteLine();
            Colorful.Console.WriteLine("**********************************************", _colorScheme.Comment);
            Colorful.Console.WriteLine();

            return _terminal;
        }

        private Terminal GetTerminal()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? Terminal.LinuxBash :
                    RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Terminal.MacBash : Terminal.Unknown;
            }
            
            var title = Colorful.Console.Title.ToLowerInvariant();
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
            Colorful.Console.WriteLine(e.ToString(), GetColor(_colorScheme.ErrorColor));
        }
        
        public void WriteErrorLine(string errorMessage)
        {
            Colorful.Console.WriteLine(errorMessage, GetColor(_colorScheme.ErrorColor));
        }
        
        public void WriteLine(object objectToWrite)
        {
            Colorful.Console.WriteLine(objectToWrite.ToString(), _colorScheme.Text);
        }

        public void Write(object objectToWrite)
        {
            Colorful.Console.Write(objectToWrite.ToString(), _colorScheme.Text);
        }

        public void WriteCommentLine(object objectToWrite)
        {
            Colorful.Console.WriteLine(objectToWrite.ToString(), _colorScheme.Comment);
        }
        
        public void WriteLessImportant(object objectToWrite)
        {
            Colorful.Console.Write(objectToWrite.ToString(), GetColor(_colorScheme.LessImportant));
        }
        
        public void WriteKeyword(string keyword)
        {
            Colorful.Console.Write(keyword, GetColor(_colorScheme.Keyword));
        }
        
        public void WriteInteresting(string interesting)
        {
            Colorful.Console.WriteLine(interesting, GetColor(_colorScheme.Interesting));
        }

        public void WriteLine()
        {
            Colorful.Console.WriteLine();
        }

        public void WriteGood(string goodText)
        {
            Colorful.Console.WriteLine(goodText, GetColor(_colorScheme.Good));
        }

        public void WriteString(object result)
        {
            Colorful.Console.WriteLine(result, GetColor(_colorScheme.String));
        }
    }
}
