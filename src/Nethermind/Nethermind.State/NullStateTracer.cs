// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State
{
    public class NullStateTracer : IStateTracer
    {
        private NullStateTracer() { }

        public static IStateTracer Instance { get; } = new NullStateTracer();
        private const string ErrorMessage = "Null tracer should never receive any calls.";

        public bool IsTracingState => false;

        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportCodeChange(Address address, byte[]? before, byte[]? after)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportAccountRead(Address address)
            => throw new InvalidOperationException(ErrorMessage);
    }
}
