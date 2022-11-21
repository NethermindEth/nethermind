// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State
{
    public interface IStateTracer
    {
        bool IsTracingState { get; }
        void ReportBalanceChange(Address address, UInt256? before, UInt256? after);
        void ReportCodeChange(Address address, byte[]? before, byte[]? after);
        void ReportNonceChange(Address address, UInt256? before, UInt256? after);
        void ReportAccountRead(Address address);
    }
}
