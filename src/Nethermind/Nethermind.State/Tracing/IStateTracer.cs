// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.Tracing
{
    public interface IStateTracer
    {
        /// <summary>
        ///
        /// </summary>
        /// <remarks>
        /// Controls
        /// - <see cref="ReportBalanceChange"/>
        /// - <see cref="ReportCodeChange"/>
        /// - <see cref="ReportNonceChange"/>
        /// - <see cref="ReportAccountRead"/>
        /// </remarks>
        bool IsTracingState { get; }

        /// <summary>
        /// Reports change of balance for address
        /// </summary>
        /// <param name="address"></param>
        /// <param name="before"></param>
        /// <param name="after"></param>
        /// <remarks>Depends on <see cref="IsTracingState"/></remarks>
        void ReportBalanceChange(Address address, UInt256? before, UInt256? after);

        /// <summary>
        /// Reports change of code for address
        /// </summary>
        /// <param name="address"></param>
        /// <param name="before"></param>
        /// <param name="after"></param>
        /// <remarks>Depends on <see cref="IsTracingState"/></remarks>
        void ReportCodeChange(Address address, byte[]? before, byte[]? after);

        /// <summary>
        /// Reports change of nonce for address
        /// </summary>
        /// <param name="address"></param>
        /// <param name="before"></param>
        /// <param name="after"></param>
        /// <remarks>Depends on <see cref="IsTracingState"/></remarks>
        void ReportNonceChange(Address address, UInt256? before, UInt256? after);

        /// <summary>
        /// Reports accessing the address
        /// </summary>
        /// <param name="address"></param>
        /// <remarks>Depends on <see cref="IsTracingState"/></remarks>
        void ReportAccountRead(Address address);
    }
}
