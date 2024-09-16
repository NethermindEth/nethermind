// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Facade.Eth.RpcTransaction;

// TODO: We might want to lift this to `Core`
public interface IToTransaction<in T>
{
    Transaction ToTransaction(T t);
    Transaction ToTransactionWithDefaults(T t, ulong chainId);
}
