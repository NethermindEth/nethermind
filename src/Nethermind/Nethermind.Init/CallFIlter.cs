// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.TxPool;
using Nethermind.TxPool.Filters;

namespace Nethermind.Init;

internal sealed class CallFilter:  IIncomingTxFilter
{
    private readonly Dictionary<AddressAsKey, HashSet<string>> _blacklistedFunctionCalls = new();
    private readonly IDebugBridge _blockchainBridge;
    public CallFilter(string[] blackList, IDebugBridge bridge)
    {
        foreach (var stuff in blackList)
        {
            var data = stuff.Split(';');
            _blacklistedFunctionCalls[new AddressAsKey(new Address(data[0]))] = new HashSet<string>(data[1..]);
        }
        _blockchainBridge = bridge;
    }
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        var options = new GethTraceOptions() { Tracer = NativeCallTracer.CallTracer };
        GethLikeTxTrace? trace = _blockchainBridge.GetTransactionTrace(tx, BlockParameter.Latest, CancellationToken.None,
            options);

        var traces = (NativeCallTracerCallFrame)(trace!.CustomTracerResult!.Value);
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
