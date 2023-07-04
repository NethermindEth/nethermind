// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus
{
    public class Signer : ISigner, ISignerStore
    {
        private readonly ulong _chainId;
        private PrivateKey? _key;
        private readonly ILogger _logger;

        public Address Address => _key?.Address ?? Address.Zero;

        public bool CanSign => _key is not null;

        public Signer(ulong chainId, PrivateKey key, ILogManager logManager)
        {
            _chainId = chainId;
            _logger = logManager?.GetClassLogger<Signer>() ?? throw new ArgumentNullException(nameof(logManager));
            SetSigner(key);
        }

        public Signer(ulong chainId, ProtectedPrivateKey key, ILogManager logManager)
        {
            _chainId = chainId;
            _logger = logManager?.GetClassLogger<Signer>() ?? throw new ArgumentNullException(nameof(logManager));
            SetSigner(key);
        }

        public Signature Sign(Keccak message)
        {
            if (!CanSign) throw new InvalidOperationException("Cannot sign without provided key.");
            byte[] rs = SpanSecP256k1.SignCompact(message.Bytes, _key!.KeyBytes, out int v);
            return new Signature(rs, v);
        }

        public ValueTask Sign(Transaction tx)
        {
            Keccak hash = Keccak.Compute(Rlp.Encode(tx, true, true, _chainId).Bytes);
            tx.Signature = Sign(hash);
            tx.Signature.V = tx.Type == TxType.Legacy ? tx.Signature.V + 8 + 2 * _chainId : (ulong)(tx.Signature.RecoveryId + 27);
            return default;
        }

        public PrivateKey? Key => _key is null ? null : new PrivateKey(_key.KeyBytes);

        public void SetSigner(PrivateKey? key)
        {
            _key = key;
            if (_logger.IsInfo) _logger.Info(
                _key is not null ? $"Address {Address} is configured for signing blocks." : "No address is configured for signing blocks.");
        }

        public void SetSigner(ProtectedPrivateKey? key)
        {
            PrivateKey? pk = null;
            if (key is not null)
            {
                pk = key.Unprotect();
            }

            SetSigner(pk);
        }
    }
}
