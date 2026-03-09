// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.TxPool;

public interface IAccountFundsAugmentor
{
    UInt256 GetAdditionalFunds(Transaction tx);
}

public sealed class NullAccountFundsAugmentor : IAccountFundsAugmentor
{
    public static IAccountFundsAugmentor Instance { get; } = new NullAccountFundsAugmentor();

    private NullAccountFundsAugmentor() { }

    public UInt256 GetAdditionalFunds(Transaction tx) => UInt256.Zero;
}
