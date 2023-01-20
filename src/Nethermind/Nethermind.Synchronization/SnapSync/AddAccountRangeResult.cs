// Copyright 2022 Demerzel Solutions Limited
// Licensed under the LGPL-3.0. For full terms, see LICENSE-LGPL in the project root.

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync
{
    internal class AddAccountRangeResult : AddStorageRangeResult
    {
        public IList<PathWithAccount> AccountsWithStorage { get; private set; }
        public IList<Keccak> CodeHashes { get; private set; }

        public AddAccountRangeResult(AddRangeResult result, bool moreChildrenToRight, IList<PathWithAccount> accountsWithStorage, IList<Keccak> codeHashes) :
            base(result, moreChildrenToRight)
        {
            AccountsWithStorage = accountsWithStorage;
            CodeHashes = codeHashes;
        }
    }
}
