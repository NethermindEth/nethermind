// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;

namespace Nethermind.State.Snap
{
    public class AccountsAndProofs : IDisposable
    {
        private IPathWithAccountList _pathAndAccounts = EmptyPathWithAccountList.Instance;
        private IByteArrayList _proofs = EmptyByteArrayList.Instance;

        [AllowNull]
        public IPathWithAccountList PathAndAccounts
        {
            get => _pathAndAccounts;
            set => _pathAndAccounts = value ?? EmptyPathWithAccountList.Instance;
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
