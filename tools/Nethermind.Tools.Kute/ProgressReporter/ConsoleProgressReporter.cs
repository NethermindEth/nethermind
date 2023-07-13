// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Tools.Kute.ProgressReporter;

public class ConsoleProgressReporter : IProgressReporter
{
    private const string Infix = "/";

    private readonly int _total;

    private int? _lastIndex;

    public ConsoleProgressReporter(int total)
    {
        _total = total;
    }

    public void ReportProgress(int n)
    {
        var sb = new StringBuilder();

        if (_lastIndex == null)
        {
            sb.Append("Processed: ");
        }
        else
        {
            sb.Append('\b', _lastIndex!.ToString()!.Length + Infix.Length + _total.ToString().Length);
        }
        sb.Append(n);
        sb.Append(Infix);
        sb.Append(_total);

        Console.Write(sb);
        _lastIndex = n;
    }

    public void ReportComplete()
    {
        Console.Write('\n');
    }
}
