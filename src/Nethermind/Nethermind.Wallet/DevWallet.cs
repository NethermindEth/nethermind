/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Logging;
using Nethermind.Secp256k1;

namespace Nethermind.Wallet
{
    [DoNotUseInSecuredContext("For dev purposes only")]
    public class DevWallet : IWallet
    {
        private static byte[] _keySeed = new byte[32];
        private readonly ILogger _logger;

        private Dictionary<Address, PrivateKey> _keys = new Dictionary<Address, PrivateKey>();

        public DevWallet(ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _keySeed[31] = 1;
            for (int i = 0; i < 10; i++)
            {
                PrivateKey key = new PrivateKey(_keySeed);
                _keys.Add(key.Address, key);
                _keySeed[31]++;
            }
        }

        public Address[] GetAccounts()
        {
            return _keys.Keys.ToArray();
        }

        public void Sign(Transaction tx, int chainId)
        {
            if (_logger.IsDebug) _logger?.Debug($"Signing transaction: {tx.Value} to {tx.To}");
            Keccak hash = Keccak.Compute(Rlp.Encode(tx, true, true, chainId));
            tx.Signature = Sign(tx.SenderAddress, hash);
            tx.Signature.V = (byte) (tx.Signature.V + 8 + 2 * chainId);
        }

        public Signature Sign(Address address, Keccak message)
        {
            var rs = Proxy.SignCompact(message.Bytes, _keys[address].KeyBytes, out int v);
            return new Signature(rs, v);
        }
    }
}