// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
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

        public bool CanSignHeader => false;

        public Signer(ulong chainId, PrivateKey? key, ILogManager logManager)
        {
            _chainId = chainId;
            _logger = logManager.GetClassLogger<Signer>();
            SetSigner(key);
        }

        public Signer(ulong chainId, IProtectedPrivateKey key, ILogManager logManager)
        {
            _chainId = chainId;
            _logger = logManager?.GetClassLogger<Signer>() ?? throw new ArgumentNullException(nameof(logManager));
            SetSigner(key);
        }

        public bool TrySign(in ValueHash256 message, [NotNullWhen(true)] out Signature signature)
        {
            if (_key is null)
            {
                signature = null!;
                return false;
            }
            byte[] rs = SecP256k1.SignCompact(message.Bytes, _key.KeyBytes, out int v)
                ?? throw new InvalidOperationException("Failed to sign the message.");
            signature = new Signature(rs, v);
            return true;
        }

        public bool TrySign(Transaction tx)
        {
            ValueHash256 hash = ValueKeccak.Compute(Rlp.Encode(tx, true, true, _chainId).Bytes);
            if (!TrySign(in hash, out Signature sig)) return false;
            sig.V = tx.Type == TxType.Legacy ? sig.V + 8 + 2 * _chainId : (ulong)(sig.RecoveryId + 27);
            tx.Signature = sig;
            return true;
        }

        public PrivateKey? Key => _key is null ? null : new PrivateKey(_key.KeyBytes);

        public void SetSigner(PrivateKey? key)
        {
            _key = key;
            if (_logger.IsInfo) _logger.Info(
                _key is not null ? $"Address {Address} is configured for signing blocks." : "No address is configured for signing blocks.");
        }

        public void SetSigner(IProtectedPrivateKey? key)
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
