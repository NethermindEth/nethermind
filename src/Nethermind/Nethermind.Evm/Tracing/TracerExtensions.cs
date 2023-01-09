// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Evm.Tracing
{
    public static class TracerExtensions
    {
        public static CancellationTxTracer WithCancellation(this ITxTracer txTracer, CancellationToken cancellationToken, bool setDefaultCancellations = true)
        {
            return !setDefaultCancellations
                ? new(txTracer, cancellationToken)
                : new(txTracer, cancellationToken)
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
