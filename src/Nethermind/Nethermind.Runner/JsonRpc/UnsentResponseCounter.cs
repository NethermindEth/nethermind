// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Runner.JsonRpc;

internal sealed class UnsentResponseCounter
{
    internal const int CallerShareDivisor = 16;
    internal static readonly long CountedResponseBytes = 1.MiB;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, (long Bytes, int Responses)> _callers = [];
    private long _bytes;

    public static long CallerLimit(long limit) => Math.Max(1, limit / CallerShareDivisor);

    public static bool IsCounted(long responseBytes) => responseBytes > CountedResponseBytes;

    public bool TryReserve(string caller, long responseBytes, long bytes, bool responseCounted, long limit, int maxResponsesPerCaller, long callerAllowance = 0)
    {
        long callerLimit = CallerLimit(limit) + callerAllowance;
        bool countsNow = !responseCounted && IsCounted(responseBytes + bytes);
        lock (_lock)
        {
            _callers.TryGetValue(caller, out (long Bytes, int Responses) state);
            bool otherResponsesUnsent = state.Bytes > responseBytes;
            if ((otherResponsesUnsent && state.Bytes + bytes > callerLimit)
                || _bytes + bytes > limit
                || (countsNow && maxResponsesPerCaller > 0 && state.Responses >= maxResponsesPerCaller))
            {
                return false;
            }

            _callers[caller] = (state.Bytes + bytes, countsNow ? state.Responses + 1 : state.Responses);
            _bytes += bytes;
            return true;
        }
    }

    public void Release(string caller, long bytes, bool responseCounted)
    {
        lock (_lock)
        {
            if (!_callers.TryGetValue(caller, out (long Bytes, int Responses) state))
            {
                return;
            }

            long released = Math.Min(bytes, state.Bytes);
            (long Bytes, int Responses) next = (state.Bytes - released, responseCounted ? Math.Max(0, state.Responses - 1) : state.Responses);
            if (next is { Bytes: 0, Responses: 0 })
            {
                _callers.Remove(caller);
            }
            else
            {
                _callers[caller] = next;
            }

            _bytes -= released;
        }
    }
}
