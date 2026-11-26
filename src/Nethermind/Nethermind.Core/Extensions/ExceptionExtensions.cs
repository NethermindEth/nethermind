// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.ObjectModel;
using System.Text;

namespace Nethermind.Core.Extensions;

public static class ExceptionExtensions
{
    /// <summary>
    /// Returns the type and message of <paramref name="exception"/> and of each inner exception, joined by
    /// <c> ---&gt; </c>, for at most <paramref name="maxDepth"/> exceptions; a longer chain ends with <c> ---&gt; ...</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDepth"/> is zero or negative.</exception>
    public static string DescribeChain(this Exception exception, int maxDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDepth);

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

    /// <summary>Returns whether <paramref name="exception"/> is a <typeparamref name="T"/> or wraps one.</summary>
    /// <remarks>
    /// Follows <see cref="Exception.InnerException"/> to any depth and, through an <see cref="AggregateException"/>, each of
    /// its <see cref="AggregateException.InnerExceptions"/>, of which <see cref="Exception.InnerException"/> gives only the first.
    /// </remarks>
    public static bool IsOrWraps<T>(this Exception exception) where T : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is T)
                return true;

            if (current is AggregateException aggregate)
            {
                ReadOnlyCollection<Exception> inner = aggregate.InnerExceptions;
                for (int i = 0; i < inner.Count; i++)
                {
                    if (inner[i].IsOrWraps<T>())
                        return true;
                }

                return false;
            }
        }

        return false;
    }
}
