//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State
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
