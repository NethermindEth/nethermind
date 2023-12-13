// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization.Test.ParallelSync
{
    public partial class MultiSyncModeSelectorTestsBase
    {
        public enum FastBlocksState
        {
            None,
            FinishedHeaders,
            FinishedBodies,
            FinishedReceipts
        }

        protected readonly bool _needToWaitForHeaders;

        public MultiSyncModeSelectorTestsBase(bool needToWaitForHeaders)
        {
            _needToWaitForHeaders = needToWaitForHeaders;
        }

        protected SyncMode GetExpectationsIfNeedToWaitForHeaders(SyncMode expectedSyncModes)
        {
            if (_needToWaitForHeaders && (expectedSyncModes & SyncMode.FastHeaders) == SyncMode.FastHeaders)
            {
                expectedSyncModes &= ~SyncMode.StateNodes;
                expectedSyncModes &= ~SyncMode.SnapSync;
                expectedSyncModes &= ~SyncMode.Full;
                expectedSyncModes &= ~SyncMode.FastSync;
            }

            return expectedSyncModes;
        }
    }
}
