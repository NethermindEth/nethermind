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
using System.Drawing;
using Console = Colorful.Console;

namespace Nethermind.Cli
{
    public static class CliConsole
    {
        private static ColorScheme _colorScheme;

        public static void Init(ColorScheme colorScheme)
        {
            _colorScheme = colorScheme;
            Console.BackgroundColor = colorScheme.BackgroundColor;
            Console.ForegroundColor = colorScheme.Text;
            Console.Clear();

            Console.WriteLine("**********************************************", _colorScheme.Comment);
            Console.WriteLine();
            Console.WriteLine("Nethermind CLI", _colorScheme.Good);
            Console.WriteLine("  https://github.com/NethermindEth/nethermind", _colorScheme.Interesting);
            Console.WriteLine("  https://nethermind.readthedocs.io/en/latest/", _colorScheme.Interesting);
            Console.WriteLine();
            Console.WriteLine("powered by:", _colorScheme.Text);
            Console.WriteLine("  https://github.com/sebastienros/jint", _colorScheme.Interesting);
            Console.WriteLine("  https://github.com/tomakita/Colorful.Console", _colorScheme.Interesting);
            Console.WriteLine("  https://github.com/tonerdo/readline", _colorScheme.Interesting);
            Console.WriteLine();
            Console.WriteLine("**********************************************", _colorScheme.Comment);
            Console.WriteLine();
        }

        public static void WriteException(Exception e)
        {
            Console.WriteLine(e.ToString(), _colorScheme.ErrorColor);
        }
        
        public static void WriteErrorLine(string errorMessage)
        {
            Console.WriteLine(errorMessage, _colorScheme.ErrorColor);
        }
        
        public static void WriteLine(object objectToWrite)
        {
            Console.WriteLine(objectToWrite.ToString(), _colorScheme.Text);
        }
        
        public static void WriteCommentLine(object objectToWrite)
        {
            Console.WriteLine(objectToWrite.ToString(), _colorScheme.Comment);
        }
        
        public static void WriteLessImportant(object objectToWrite)
        {
            Console.Write(objectToWrite.ToString(), _colorScheme.LessImportant);
        }
        
        public static void WriteKeyword(string keyword)
        {
            Console.Write(keyword, _colorScheme.Keyword);
        }
        
        public static void WriteInteresting(string interesting)
        {
            Console.Write(interesting, _colorScheme.Interesting);
        }

        public static void WriteLine()
        {
            Console.WriteLine();
        }

        public static void WriteGood(string goodText)
        {
            Console.WriteLine(goodText, _colorScheme.Good);
        }

        public static void WriteString(object result)
        {
            Console.WriteLine(result, _colorScheme.String);
        }
    }
}