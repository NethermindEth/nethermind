// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Facade.Eth.RpcTransaction;

public interface ITransactionConverter<out T>
{
    T FromTransaction(Transaction tx);
}
