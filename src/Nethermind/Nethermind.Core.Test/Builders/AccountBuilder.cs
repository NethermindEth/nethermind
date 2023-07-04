// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core.Test.Builders
{
    public class AccountBuilder : BuilderBase<Account>
    {
        public AccountBuilder()
        {
            TestObjectInternal = Account.TotallyEmpty;
        }

        public AccountBuilder WithBalance(UInt256 balance)
        {
            TestObjectInternal = TestObjectInternal.WithChangedBalance(balance);
            return this;
        }

        public AccountBuilder WithNonce(UInt256 nonce)
        {
            TestObjectInternal = TestObjectInternal.WithChangedNonce(nonce);
            return this;
        }

        public AccountBuilder WithCode(byte[] code)
        {
            TestObjectInternal = TestObjectInternal.WithChangedCodeHash(Keccak.Compute(code));
            return this;
        }

        public AccountBuilder WithStorageRoot(Keccak storageRoot)
        {
            TestObjectInternal = TestObjectInternal.WithChangedStorageRoot(storageRoot);
            return this;
        }
    }
}
