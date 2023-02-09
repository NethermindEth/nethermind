// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Linq;
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

            public AddressNonces(in UInt256 startNonce)
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
