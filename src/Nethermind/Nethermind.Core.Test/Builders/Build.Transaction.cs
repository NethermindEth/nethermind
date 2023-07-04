// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Core.Test.Builders;

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
