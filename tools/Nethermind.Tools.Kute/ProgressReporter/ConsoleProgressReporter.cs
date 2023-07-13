// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Tools.Kute.ProgressReporter;

public class ConsoleProgressReporter : IProgressReporter
{
    private readonly string _suffix;

    private int _lastN;

    public ConsoleProgressReporter(int total)
    {
        _suffix = $"/{total}";
    }

    public void ReportStart()
    {
        Console.Write($"Progress: 0{_suffix}");
    }

    public void ReportProgress(int n)
    {
        var sb = new StringBuilder();

        sb.Append('\b', _lastN.ToString().Length + _suffix.Length);
        sb.Append(n);
        sb.Append(_suffix);

        Console.Write(sb);
        _lastN = n;
    }

    public void ReportComplete()
    {
        Console.Write('\n');
    }
}
