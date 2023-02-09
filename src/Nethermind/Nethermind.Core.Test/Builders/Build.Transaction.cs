// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Test.Builders
{
    public partial class Build
    {
        public TransactionBuilder<Transaction> Transaction => new();
        public TransactionBuilder<SystemTransaction> SystemTransaction => new();
        public TransactionBuilder<GeneratedTransaction> GeneratedTransaction => new();
        public TransactionBuilder<T> TypedTransaction<T>() where T : Transaction, new() => new();

        public TransactionBuilder<NamedTransaction> NamedTransaction(string name)
        {
            return new() { TestObjectInternal = { Name = name } };
        }
    }
}
