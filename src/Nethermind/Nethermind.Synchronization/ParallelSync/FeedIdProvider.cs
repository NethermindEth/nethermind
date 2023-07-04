// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Synchronization.ParallelSync
{
    public static class FeedIdProvider
    {
        private static int _dataConsumerId;

        public static int AssignId()
        {
            return Interlocked.Increment(ref _dataConsumerId);
        }
    }
}
