//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Secp256k1;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus
{
    public class Signer : ISigner, ISignerStore
    {
        private readonly ulong _chainId;
        private PrivateKey? _key;
        private readonly ILogger _logger;

        public Address Address => _key?.Address ?? Address.Zero;

        public bool CanSign => _key != null;

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
            byte[] rs = Proxy.SignCompact(message.Bytes, _key!.KeyBytes, out int v);
            return new Signature(rs, v);
        }

        public ValueTask Sign(Transaction tx)
        {
            Keccak hash = Keccak.Compute(Rlp.Encode(tx, true, true, _chainId).Bytes);
            tx.Signature = Sign(hash);
            tx.Signature.V = tx.Signature.V + 8 + 2 * _chainId;
            return default;
        }

        public PrivateKey? Key => _key is null ? null : new PrivateKey(_key.KeyBytes); 

        public void SetSigner(PrivateKey? key)
        {
            _key = key;
            if (_logger.IsInfo) _logger.Info(
                _key != null ? $"Address {Address} is configured for signing blocks." : "No address is configured for signing blocks.");
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
