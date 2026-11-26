// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.State
{
    public partial class StateTree
    {
        /// <remarks>
        /// One at a time, deletions last - the guest's counterpart of the bulk set: on its one core, sorting the entries
        /// and recursing over them costs more than the shared descent saves. Deletions go last, as storage writes do in
        /// <c>ProcessStorageChanges</c>, so a branch is only collapsed once every insert has landed.
        /// </remarks>
        internal partial void WriteAccounts(Dictionary<AddressAsKey, Account?> accounts)
        {
            foreach (KeyValuePair<AddressAsKey, Account?> kv in accounts)
            {
                if (kv.Value is not null) Set(kv.Key.Value, kv.Value);
            }

            foreach (KeyValuePair<AddressAsKey, Account?> kv in accounts)
            {
                if (kv.Value is null) Set(kv.Key.Value, null);
            }
        }
    }
}
