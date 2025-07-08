// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.Call;

namespace Nethermind.TxPool.Filters;

internal sealed class CallFilter:  IIncomingTxFilter
{
    private readonly Dictionary<AddressAsKey, HashSet<string>> _blacklistedFunctionCalls = new();
    public CallFilter(string[] blackList)
    {
        foreach (var stuff in blackList)
        {
            var data = stuff.Split(';');
            _blacklistedFunctionCalls[new AddressAsKey(new Address(data[0]))] = new HashSet<string>(data[1..]);
        }
    }
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        // generate a trace
        GethLikeTxTrace trace = new();

        var traces = (NativeCallTracerCallFrame)(trace.CustomTracerResult!.Value);
        if (_blacklistedFunctionCalls.Count != 0)
        {
            if (!IsFrameValid(_blacklistedFunctionCalls, traces) || traces.Calls.Any(tr => !IsFrameValid(_blacklistedFunctionCalls, traces)))
                return AcceptTxResult.BlacklistedAddress;
        }
        return AcceptTxResult.Accepted;
    }

    private static bool IsFrameValid(Dictionary<AddressAsKey, HashSet<string>> list, NativeCallTracerCallFrame frame)
    {
        if (list.TryGetValue(new AddressAsKey(frame.To!), out var value))
        {
            var selector = frame.Input!.AsSpan()[..4];
            if (value.Contains(selector.ToHexString())) return false;
        }

        return true;
    }
}
