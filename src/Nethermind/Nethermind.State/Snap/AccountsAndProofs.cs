// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;

namespace Nethermind.State.Snap
{
    public class AccountsAndProofs : IDisposable
    {
        private IOwnedReadOnlyList<PathWithAccount> _pathAndAccounts = IOwnedReadOnlyList<PathWithAccount>.Empty;
        private IByteArrayList _proofs = EmptyByteArrayList.Instance;

        [AllowNull]
        public IOwnedReadOnlyList<PathWithAccount> PathAndAccounts
        {
            get => _pathAndAccounts;
            set => _pathAndAccounts = value ?? IOwnedReadOnlyList<PathWithAccount>.Empty;
        }

        [AllowNull]
        public IByteArrayList Proofs
        {
            get => _proofs;
            set => _proofs = value ?? EmptyByteArrayList.Instance;
        }

        public void Dispose()
        {
            PathAndAccounts.Dispose();
            Proofs.Dispose();
        }
    }
}
