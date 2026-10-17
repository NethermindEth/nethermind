// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Threading;

public partial class ParallelUnbalancedWork
{
    internal static partial WorkerGroup? GetCurrentGroup() => null;

    internal sealed partial class WorkerGroup
    {
        private partial void Initialize() { }
        internal partial WorkerScope Enter() => new(1);
        internal partial void Queue(System.Threading.IThreadPoolWorkItem work) => work.Execute();
        internal partial void KeepRunners(System.TimeSpan hold) { }
        internal partial void ReleaseRunners() { }
    }

    public sealed partial class WorkerScope
    {
        internal WorkerScope(int concurrency, bool limitConcurrency = false) { }

        public partial void Dispose() { }
    }
}
