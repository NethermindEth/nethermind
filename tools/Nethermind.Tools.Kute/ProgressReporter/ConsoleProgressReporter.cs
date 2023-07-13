// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Tools.Kute.ProgressReporter;

public class ConsoleProgressReporter : IProgressReporter
{
    private readonly string _suffix;

    private int? _lastIndex;

    public ConsoleProgressReporter(int total)
    {
        _suffix = $"/{total}";
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
            sb.Append('\b', _lastIndex!.ToString()!.Length + _suffix.Length);
        }
        sb.Append(n);
        sb.Append(_suffix);

        Console.Write(sb);
        _lastIndex = n;
    }

    public void ReportComplete()
    {
        Console.Write('\n');
    }
}
