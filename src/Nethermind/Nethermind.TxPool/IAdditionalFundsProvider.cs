// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.TxPool;

public interface IAdditionalFundsProvider
{
    UInt256 GetAdditionalFunds(Transaction tx);
}

public sealed class NullAdditionalFundsProvider : IAdditionalFundsProvider
{
    public static IAdditionalFundsProvider Instance { get; } = new NullAdditionalFundsProvider();

    private NullAdditionalFundsProvider() { }

    public UInt256 GetAdditionalFunds(Transaction tx) => UInt256.Zero;
}
