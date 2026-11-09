// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;

namespace Nethermind.Core.Extensions;

public static class ExceptionExtensions
{
    /// <summary>
    /// Returns the type and message of <paramref name="exception"/> and of each inner exception, joined by
    /// <c> ---&gt; </c>, for at most <paramref name="maxDepth"/> exceptions; a longer chain ends with <c> ---&gt; ...</c>.
    /// </summary>
    public static string DescribeChain(this Exception exception, int maxDepth)
    {
        StringBuilder text = new();
        Exception? current = exception;
        for (int depth = 0; current is not null && depth < maxDepth; current = current.InnerException, depth++)
        {
            if (depth != 0) text.Append(" ---> ");
            text.Append(current.GetType()).Append(": ").Append(current.Message);
        }

        if (current is not null) text.Append(" ---> ...");
        return text.ToString();
    }
}
