// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.Wallet
{
    public class WalletTxSigner : ITxSigner
    {
        private readonly IWallet _wallet;
        private readonly ulong _chainId;

        public WalletTxSigner(IWallet wallet, ulong chainId)
        {
            _wallet = wallet;
            _chainId = chainId;
        }

        public ValueTask Sign(Transaction tx)
        {
            _wallet.Sign(tx, _chainId);
            return default;
        }
    }
}
