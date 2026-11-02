// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;

namespace Nethermind.State
{
    public partial class StateTree
    {
        internal partial void WriteAccounts(Dictionary<AddressAsKey, Account?> accounts)
        {
            if (Avx2.IsSupported && accounts.Count >= KeyHashBatch.MinimumBatchSize)
            {
                SetAccounts(accounts);
            }
            else
            {
                using StateTreeBulkSetter stateSetter = BeginSet(accounts.Count);
                foreach (KeyValuePair<AddressAsKey, Account?> kv in accounts)
                {
                    stateSetter.Set(kv.Key, kv.Value);
                }
            }
        }
    }
}
