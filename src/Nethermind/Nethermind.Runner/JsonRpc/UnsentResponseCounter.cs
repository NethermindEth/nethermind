// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;

namespace Nethermind.Runner.JsonRpc;

internal sealed class UnsentResponseCounter
{
    internal const int CallerShareDivisor = 16;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, (long Bytes, int Responses)> _callers = [];
    private long _bytes;

    public static long CallerLimit(long limit) => Math.Max(1, limit / CallerShareDivisor);

    public bool TryReserve(string caller, long bytes, bool isNewResponse, long limit, int maxResponsesPerCaller, long callerAllowance = 0)
    {
        long callerLimit = CallerLimit(limit) + callerAllowance;
        lock (_lock)
        {
            _callers.TryGetValue(caller, out (long Bytes, int Responses) state);
            if (state.Bytes + bytes > callerLimit
                || _bytes + bytes > limit
                || (isNewResponse && maxResponsesPerCaller > 0 && state.Responses >= maxResponsesPerCaller))
            {
                return false;
            }

            _callers[caller] = (state.Bytes + bytes, isNewResponse ? state.Responses + 1 : state.Responses);
            _bytes += bytes;
            return true;
        }
    }

    public void Release(string caller, long bytes)
    {
        lock (_lock)
        {
            if (!_callers.TryGetValue(caller, out (long Bytes, int Responses) state))
            {
                return;
            }

            long released = Math.Min(bytes, state.Bytes);
            if (state.Responses <= 1)
            {
                _callers.Remove(caller);
            }
            else
            {
                _callers[caller] = (state.Bytes - released, state.Responses - 1);
            }

            _bytes -= released;
        }
    }
}
