// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;

namespace Nethermind.State.Snap
{
    public class AccountsAndProofs : IDisposable
    {
        public IOwnedReadOnlyList<PathWithAccount> PathAndAccounts { get; set; }
        public IOwnedReadOnlyList<byte[]> Proofs { get; set; }

        public void Dispose()
        {
            PathAndAccounts?.Dispose();
            Proofs?.Dispose();
        }
    }
}
