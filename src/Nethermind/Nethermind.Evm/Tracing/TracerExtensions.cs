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
// 

using System.Threading;

namespace Nethermind.Evm.Tracing
{
    public static class TracerExtensions
    {
        public static CancellationTxTracer WithCancellation(this ITxTracer txTracer, CancellationToken cancellationToken, bool setDefaultCancellations = true)
        {
            return !setDefaultCancellations
                ? new(txTracer, cancellationToken)
                : new (txTracer, cancellationToken)
                {
                    IsTracingActions = true,
                    IsTracingOpLevelStorage = true,
                    IsTracingInstructions = true, // a little bit costly but almost all are simple calls
                    IsTracingRefunds = true
                };
        }

        public static CancellationBlockTracer WithCancellation(this IBlockTracer blockTracer, CancellationToken cancellationToken) =>
            new(blockTracer, cancellationToken);
    }
}
