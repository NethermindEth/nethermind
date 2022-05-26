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
