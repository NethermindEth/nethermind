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

using System.Globalization;
using System.Numerics;
using Nethermind.Core.Encoding;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Crypto
{
    /// <summary>
    ///     for ecdsa tests
    ///     http://blog.enuma.io/update/2016/11/01/a-tale-of-two-curves-hardware-signing-for-ethereum.html
    /// </summary>
    public class EthereumEcdsa : Ecdsa, IEthereumEcdsa
    {
        public static readonly BigInteger MaxLowS = BigInteger.Parse("7FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF5D576E7357A4501DDFE92F46681B20A0", NumberStyles.HexNumber);
        public static readonly BigInteger LowSTransform = BigInteger.Parse("00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", NumberStyles.HexNumber);

        private readonly int _chainIdValue;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;

        public EthereumEcdsa(ISpecProvider specProvider, ILogManager logManager)
        {
            _specProvider = specProvider;
            _logger = logManager.GetClassLogger();
            _chainIdValue = specProvider.ChainId;
        }
        
        public void Sign(PrivateKey privateKey, Transaction transaction, UInt256 blockNumber)
        {
            _logger?.Debug($"Signing transaction: {transaction.Value} to {transaction.To} with data {transaction.Data}");
            bool isEip155Enabled = _specProvider.GetSpec(blockNumber).IsEip155Enabled;
            Keccak hash = Keccak.Compute(Rlp.Encode(transaction, true, isEip155Enabled, _chainIdValue));
            transaction.Signature = Sign(privateKey, hash);
            if (isEip155Enabled)
            {
                transaction.Signature.V = transaction.Signature.V + 8 + 2 * _chainIdValue;
            }
            
            _logger?.Debug("Transaction signed");
        }

        public bool Verify(Address sender, Transaction transaction, UInt256 blockNumber)
        {
            bool isEip155Enabled = _specProvider.GetSpec(blockNumber).IsEip155Enabled;
            Keccak hash = Keccak.Compute(Rlp.Encode(transaction, true, isEip155Enabled, _chainIdValue));
            Address recovered = RecoverAddress(transaction.Signature, hash);
            return recovered.Equals(sender);
        }

        public Address RecoverAddress(Transaction transaction, UInt256 blockNumber)
        {
            bool isEip155Enabled = _specProvider.GetSpec(blockNumber).IsEip155Enabled;
            bool applyEip155 = isEip155Enabled && (transaction.Signature.V == _chainIdValue * 2 + 35 || transaction.Signature.V == _chainIdValue * 2 + 36);  
            Keccak hash = Keccak.Compute(Rlp.Encode(transaction, true, applyEip155, _chainIdValue));
            return RecoverAddress(transaction.Signature, hash);
        }

        public Address RecoverAddress(Signature signature, Keccak message)
        {
            return RecoverPublicKey(signature, message)?.Address;
        }
    }
}