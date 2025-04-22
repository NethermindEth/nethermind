// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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

        // TODO: check if this is not being used in tests where the actual code is being used somewhere
        public AccountBuilder WithCode(byte[] code)
        {
            TestObjectInternal = TestObjectInternal.WithChangedCodeHash((UInt256)code.Length, Keccak.Compute(code));
            return this;
        }

        public AccountBuilder WithStorageRoot(Hash256 storageRoot)
        {
            TestObjectInternal = TestObjectInternal.WithChangedStorageRoot(storageRoot);
            return this;
        }
    }
}
