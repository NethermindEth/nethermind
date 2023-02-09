// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Vault
{
    public class VaultTxSigner : ITxSigner
    {
        private readonly IVaultWallet _vaultWallet;
        private readonly ulong _chainId;

        public VaultTxSigner(IVaultWallet vaultWallet, ulong chainId)
        {
            _vaultWallet = vaultWallet ?? throw new ArgumentNullException(nameof(vaultWallet));
            _chainId = chainId;
        }

        public async ValueTask Sign(Transaction tx)
        {
            Keccak hash = Keccak.Compute(Rlp.Encode(tx, true, true, _chainId).Bytes);
            tx.Signature = await _vaultWallet.Sign(tx.SenderAddress, hash);
            tx.Signature!.V = tx.Signature.V + 8 + 2 * _chainId;
        }
    }
}
