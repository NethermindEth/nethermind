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

using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Int256;

namespace Nethermind.TxPool
{
    public partial class TxPool
    {
        internal class AddressNonces
        {
            private NonceInfo _currentNonceInfo;

            public ConcurrentDictionary<UInt256, NonceInfo> Nonces { get; } = new();

            public AddressNonces(UInt256 startNonce)
            {
                _currentNonceInfo = new NonceInfo(startNonce);
                Nonces.TryAdd(_currentNonceInfo.Value, _currentNonceInfo);
            }

            public NonceInfo ReserveNonce()
            {
                UInt256 nonce = _currentNonceInfo.Value;
                NonceInfo newNonce;
                bool added = false;

                do
                {
                    nonce += 1;
                    newNonce = Nonces.AddOrUpdate(nonce, v =>
                    {
                        added = true;
                        return new NonceInfo(v);
                    }, (v, n) =>
                    {
                        added = false;
                        return n;
                    });
                } while (!added);

                if (_currentNonceInfo.Value < newNonce.Value)
                {
                    Interlocked.Exchange(ref _currentNonceInfo, newNonce);
                }
                
                return newNonce;
            }
        }
    }
}
