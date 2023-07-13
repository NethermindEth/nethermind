// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Tools.Kute.ProgressReporter;

public class UnboundedConsoleProgressReporter : IProgressReporter
{
    private int? _lastIndex;

    public void ReportProgress(int n)
    {
        var sb = new StringBuilder();

        if (_lastIndex == null)
        {
            sb.Append("Processed: ");
        }
        else
        {
            sb.Append('\b', _lastIndex!.ToString()!.Length);
        }
        sb.Append(n);

        Console.Write(sb);
        _lastIndex = n;
    }

    public void ReportComplete()
    {
        Console.Write('\n');
    }
}
