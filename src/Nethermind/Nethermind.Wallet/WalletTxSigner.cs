// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.Wallet
{
    public class WalletTxSigner(IWallet wallet, ulong chainId) : ITxSigner
    {
        private readonly IWallet _wallet = wallet;
        private readonly ulong _chainId = chainId;

        public ValueTask Sign(Transaction tx)
        {
            if (!_wallet.TrySignTransaction(tx, _chainId))
                ThrowSignFailed(tx.SenderAddress);
            return default;
        }

        [DoesNotReturn, StackTraceHidden]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ThrowSignFailed(Address sender) =>
            throw new InvalidOperationException($"Wallet failed to sign transaction for {sender}.");
    }
}
