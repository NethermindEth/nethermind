//  Copyright (c) 2022 Demerzel Solutions Limited
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
using System.Drawing;
using Nethermind.Core;

namespace Nethermind.Cli.Console;

public class ColorfulCliConsole : CliConsole
{
    private static ColorScheme _colorScheme = DraculaColorScheme.Instance;

    public ColorfulCliConsole(ColorScheme colorScheme)
    {
        _colorScheme = colorScheme;
        Colorful.Console.BackgroundColor = colorScheme.BackgroundColor;
        _terminal = PrepareConsoleForTerminal();

        if (Terminal != Terminal.Cmder)
        {
            Colorful.Console.ForegroundColor = colorScheme.Text;
        }

        Colorful.Console.WriteLine("**********************************************", _colorScheme.Comment);
        Colorful.Console.WriteLine();
        Colorful.Console.WriteLine("Nethermind CLI {0}", GetColor(_colorScheme.Good), ProductInfo.Version);
        Colorful.Console.WriteLine("  https://github.com/NethermindEth/nethermind", GetColor(_colorScheme.Interesting));
        Colorful.Console.WriteLine("  https://nethermind.readthedocs.io/en/latest/", GetColor(_colorScheme.Interesting));
        Colorful.Console.WriteLine();
        Colorful.Console.WriteLine("powered by:", _colorScheme.Text);
        Colorful.Console.WriteLine("  https://github.com/sebastienros/jint", GetColor(_colorScheme.Interesting));
        Colorful.Console.WriteLine("  https://github.com/tomakita/Colorful.Console", GetColor(_colorScheme.Interesting));
        Colorful.Console.WriteLine("  https://github.com/tonerdo/readline", GetColor(_colorScheme.Interesting));
        Colorful.Console.WriteLine();
        Colorful.Console.WriteLine("**********************************************", _colorScheme.Comment);
        Colorful.Console.WriteLine();
    }

    private Color GetColor(Color defaultColor)
    {
        return _terminal == Terminal.LinuxBash ? _colorScheme.Text : defaultColor;
    }

    public override void WriteException(Exception e)
    {
        Colorful.Console.WriteLine(e.ToString(), GetColor(_colorScheme.ErrorColor));
    }

    public override void WriteErrorLine(string errorMessage)
    {
        Colorful.Console.WriteLine(errorMessage, GetColor(_colorScheme.ErrorColor));
    }

    public override void WriteLine(object objectToWrite)
    {
        Colorful.Console.WriteLine(objectToWrite.ToString(), _colorScheme.Text);
    }

    public override void Write(object objectToWrite)
    {
        Colorful.Console.Write(objectToWrite.ToString(), _colorScheme.Text);
    }

    public override void WriteCommentLine(object objectToWrite)
    {
        Colorful.Console.WriteLine(objectToWrite.ToString(), _colorScheme.Comment);
    }

    public override void WriteLessImportant(object objectToWrite)
    {
        Colorful.Console.Write(objectToWrite.ToString(), GetColor(_colorScheme.LessImportant));
    }

    public override void WriteKeyword(string keyword)
    {
        Colorful.Console.Write(keyword, GetColor(_colorScheme.Keyword));
    }

    public override void WriteInteresting(string interesting)
    {
        Colorful.Console.WriteLine(interesting, GetColor(_colorScheme.Interesting));
    }

    public override void WriteLine()
    {
        Colorful.Console.WriteLine();
    }

    public override void WriteGood(string goodText)
    {
        Colorful.Console.WriteLine(goodText, GetColor(_colorScheme.Good));
    }

    public override void WriteString(object result)
    {
        Colorful.Console.WriteLine(result, GetColor(_colorScheme.String));
    }
}
