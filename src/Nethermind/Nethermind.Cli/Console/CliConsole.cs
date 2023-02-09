// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;

namespace Nethermind.Cli.Console;

public class CliConsole : ICliConsole
{
    protected static Terminal _terminal;
    public Terminal Terminal { get => _terminal; }

    protected static readonly Dictionary<string, Terminal> Terminals = new Dictionary<string, Terminal>
    {
        ["cmd.exe"] = Terminal.Cmd,
        ["cmd"] = Terminal.Cmder,
        ["powershell"] = Terminal.Powershell,
        ["cygwin"] = Terminal.Cygwin
    };

    public CliConsole()
    {
        _terminal = PrepareConsoleForTerminal();

        System.Console.WriteLine("**********************************************");
        System.Console.WriteLine();
        System.Console.WriteLine("Nethermind CLI {0}", ProductInfo.Version);
        System.Console.WriteLine("  https://github.com/NethermindEth/nethermind");
        System.Console.WriteLine("  https://nethermind.readthedocs.io/en/latest/");
        System.Console.WriteLine();
        System.Console.WriteLine("powered by:");
        System.Console.WriteLine("  https://github.com/sebastienros/jint");
        System.Console.WriteLine("  https://github.com/tomakita/Colorful.Console");
        System.Console.WriteLine("  https://github.com/tonerdo/readline");
        System.Console.WriteLine();
        System.Console.WriteLine("**********************************************");
        System.Console.WriteLine();
    }

    protected Terminal GetTerminal()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? Terminal.LinuxBash :
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Terminal.MacBash : Terminal.Unknown;
        }

        var title = System.Console.Title.ToLowerInvariant();
        foreach (var (key, value) in Terminals)
        {
            if (title.Contains(key))
            {
                return value;
            }
        }

        return Terminal.Unknown;
    }

    protected Terminal PrepareConsoleForTerminal()
    {
        _terminal = GetTerminal();
        if (_terminal != Terminal.Powershell)
        {
            System.Console.ResetColor();
        }

        if (_terminal != Terminal.Cygwin)
        {
            System.Console.Clear();
        }
        return _terminal;
    }

    public virtual void WriteException(Exception e)
    {
        System.Console.WriteLine(e.ToString());
    }

    public virtual void WriteErrorLine(string errorMessage)
    {
        System.Console.WriteLine(errorMessage);
    }

    public virtual void WriteLine(object objectToWrite)
    {
        System.Console.WriteLine(objectToWrite.ToString());
    }

    public virtual void Write(object objectToWrite)
    {
        System.Console.Write(objectToWrite.ToString());
    }

    public virtual void WriteCommentLine(object objectToWrite)
    {
        System.Console.WriteLine(objectToWrite.ToString());
    }

    public virtual void WriteLessImportant(object objectToWrite)
    {
        System.Console.Write(objectToWrite.ToString());
    }

    public virtual void WriteKeyword(string keyword)
    {
        System.Console.Write(keyword);
    }

    public virtual void WriteInteresting(string interesting)
    {
        System.Console.WriteLine(interesting);
    }

    public virtual void WriteLine()
    {
        System.Console.WriteLine();
    }

    public virtual void WriteGood(string goodText)
    {
        System.Console.WriteLine(goodText);
    }

    public virtual void WriteString(object result)
    {
        System.Console.WriteLine(result);
    }

    public void ResetColor()
    {
        System.Console.ResetColor();
    }
}
