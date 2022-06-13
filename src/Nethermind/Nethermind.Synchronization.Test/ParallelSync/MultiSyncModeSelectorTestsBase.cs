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
