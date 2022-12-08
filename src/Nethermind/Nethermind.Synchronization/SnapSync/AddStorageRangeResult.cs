// Copyright 2022 Demerzel Solutions Limited
// Licensed under the LGPL-3.0. For full terms, see LICENSE-LGPL in the project root.

namespace Nethermind.Synchronization.SnapSync
{
    internal class AddStorageRangeResult
    {
        public AddRangeResult Result { get; private set; }
        public bool MoreChildrenToRight { get; private set; }
        public long SyncedBytes { get; private set; }

        public AddStorageRangeResult(AddRangeResult result, bool moreChildrenToRight, long syncedBytes = 0)
        {
            Result = result;
            MoreChildrenToRight = moreChildrenToRight;
            SyncedBytes = syncedBytes;
        }
    }
}
