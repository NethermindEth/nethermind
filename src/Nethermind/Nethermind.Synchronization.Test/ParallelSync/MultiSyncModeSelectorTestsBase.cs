// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

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
