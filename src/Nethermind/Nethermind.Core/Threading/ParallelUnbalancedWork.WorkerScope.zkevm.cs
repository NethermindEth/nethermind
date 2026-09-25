// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Threading;

public partial class ParallelUnbalancedWork
{
    public sealed partial class WorkerScope
    {
        internal WorkerScope(int concurrency) { }

        public partial void Dispose() { }
    }
}
