// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

namespace Nethermind.AccountAbstraction.Source
{
    public class UserOperationSortedPool : DistinctValueSortedPool<Keccak, UserOperation, Address>
    {
        private readonly int _maximumUserOperationPerSender;

        public UserOperationSortedPool(int capacity, IComparer<UserOperation> comparer, ILogManager logManager, int maximumUserOperationPerSender) :
            base(capacity, comparer, CompetingUserOperationEqualityComparer.Instance, logManager)
        {
            _maximumUserOperationPerSender = maximumUserOperationPerSender;
        }

        protected override IComparer<UserOperation> GetUniqueComparer(IComparer<UserOperation> comparer) =>
            comparer.ThenBy(CompareUserOperationsByHash.Instance);

        protected override IComparer<UserOperation> GetGroupComparer(IComparer<UserOperation> comparer) =>
            CompareUserOperationByNonce.Instance.ThenBy(CompareUserOperationsByHash.Instance.ThenBy(comparer));

        protected override IComparer<UserOperation> GetReplacementComparer(IComparer<UserOperation> comparer) =>
            CompareReplacedUserOperationByFee.Instance.ThenBy(comparer);

        protected override Address MapToGroup(UserOperation value) => value.Sender;

        protected override Keccak GetKey(UserOperation value) => value.RequestId!;

        protected override bool AllowSameKeyReplacement => true;

        // each sender can only hold MaximumUserOperationPerSender (default 10) ops, however even if they
        // hold the maximum we still want to allow fee replacement
        public bool UserOperationWouldOverflowSenderBucket(UserOperation op)
        {
            if (GetBucketCount(op.Sender) < _maximumUserOperationPerSender)
            {
                return false;
            }

            return !CanInsert(op.RequestId!, op);
        }

        public bool CanInsert(UserOperation userOperation)
        {
            return CanInsert(userOperation.RequestId!, userOperation);
        }
    }
}
