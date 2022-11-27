// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.TxPool;

public class NonceManager : INonceManager
{
    private readonly ConcurrentDictionary<Address, AddressNonces> _nonces = new();
    private readonly IAccountStateProvider _accounts;
    private readonly ILogger _logger;

    private readonly object _locker = new();

    public NonceManager(IAccountStateProvider accounts, ILogger logger)
    {
        _logger = logger;
        _accounts = accounts;
    }

    public UInt256 ReserveNonce(Address address)
    {
        lock (_locker)
        {
            UInt256 currentNonce = 0;
            _nonces.AddOrUpdate(address, a =>
            {
                currentNonce = _accounts.GetAccount(address).Nonce;
                return new AddressNonces(currentNonce);
            }, (a, n) =>
            {
                currentNonce = n.ReserveNonce().Value;
                return n;
            });
            return currentNonce;
        }
    }

    public void ReleaseNonce(Address address, UInt256 nonce)
    {
        lock (_locker)
        {
            if (_nonces.TryGetValue(address, out AddressNonces? addressNonces))
            {
                addressNonces.Nonces.TryRemove(nonce, out _);
                if (addressNonces.Nonces.IsEmpty)
                {
                    _nonces.TryRemove(address, out _);
                }
            }
        }
    }

    public bool IsNonceUsed(Address address, UInt256 nonce)
    {
        lock (_locker)
        {
            if (!_nonces.TryGetValue(address, out var addressNonces))
            {
                return false;
            }

            if (!addressNonces.Nonces.TryGetValue(nonce, out NonceInfo? nonceInfo))
            {
                return false;
            }

            if (nonceInfo.TransactionHash is not null)
            {
                // Nonce conflict
                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Nonce: {nonce} was already used in transaction: '{nonceInfo.TransactionHash}' and cannot be reused by transaction.");

                return true;
            }
            return false;
        }
    }

    public void SetTransactionHash(Address address, UInt256 nonce, Keccak hash)
    {
        lock (_locker)
        {
            if (!_nonces.TryGetValue(address, out var addressNonces))
            {
                addressNonces = new AddressNonces(_accounts.GetAccount(address).Nonce);
                _nonces.TryAdd(address, addressNonces);
            }

            if (!addressNonces.Nonces.TryGetValue(nonce, out NonceInfo? nonceInfo))
            {
                nonceInfo = new NonceInfo(nonce);
                addressNonces.Nonces.TryAdd(nonce, nonceInfo);
            }

            nonceInfo.TransactionHash = hash;
        }
    }

    private class AddressNonces
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

    private class NonceInfo
    {
        public UInt256 Value { get; }

        public Keccak? TransactionHash { get; set; }

        public NonceInfo(in UInt256 value)
        {
            Value = value;
        }
    }
}
