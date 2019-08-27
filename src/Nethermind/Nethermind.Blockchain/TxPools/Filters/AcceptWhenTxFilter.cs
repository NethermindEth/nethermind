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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;

namespace Nethermind.Blockchain.TxPools.Filters
{
    public class AcceptWhenTxFilter : ITxFilter
    {
        private readonly Filter _filter;
        private readonly ILogger _logger;

        private AcceptWhenTxFilter(Filter filter, ILogManager logManager)
        {
            _filter = filter;
            _logger = logManager?.GetClassLogger();
        }

        public bool IsValid(Transaction transaction)
        {
            if (_filter.Nonce != null && !_filter.Nonce(transaction.Nonce))
            {
                return FalseWithLogTrace(transaction, nameof(transaction.Nonce));
            }

            if (_filter.GasPrice != null && !_filter.GasPrice(transaction.GasPrice))
            {
                return FalseWithLogTrace(transaction, nameof(transaction.GasPrice));
            }

            if (_filter.GasLimit != null && !_filter.GasLimit(transaction.GasLimit))
            {
                return FalseWithLogTrace(transaction, nameof(transaction.GasLimit));
            }

            if (_filter.Hash != null && !_filter.Hash(transaction.Hash))
            {
                return FalseWithLogTrace(transaction, nameof(transaction.Hash));
            }

            if (_filter.DeliveredBy != null && !_filter.DeliveredBy(transaction.DeliveredBy))
            {
                return FalseWithLogTrace(transaction, nameof(transaction.DeliveredBy));
            }

            if (_filter.To != null && !_filter.To(transaction.To))
            {
                return FalseWithLogTrace(transaction, nameof(transaction.To));
            }

            if (_filter.Value != null && !_filter.Value(transaction.Value))
            {
                return FalseWithLogTrace(transaction, nameof(transaction.Value));
            }

            if (_filter.Data != null && !_filter.Data(transaction.Data))
            {
                return FalseWithLogTrace(transaction, nameof(transaction.Data));
            }

            if (_filter.Init != null && !_filter.Init(transaction.Init))
            {
                return FalseWithLogTrace(transaction, nameof(transaction.Init));
            }

            if (_filter.SenderAddress != null && !_filter.SenderAddress(transaction.SenderAddress))
            {
                return FalseWithLogTrace(transaction, nameof(transaction.SenderAddress));
            }

            if (_filter.Signature != null && !_filter.Signature(transaction.Signature))
            {
                return FalseWithLogTrace(transaction, nameof(transaction.Signature));
            }

            return true;
        }

        private bool FalseWithLogTrace(Transaction transaction, string parameter)
        {
            if (_logger == null || !_logger.IsTrace)
            {
                return false;
            }

            _logger.Trace($"Transaction: {transaction.Hash} is invalid, parameter: {parameter}.");

            return false;
        }

        public static Builder Create(ILogManager logManager = null) => new Builder(logManager);

        public class Builder
        {
            private readonly ILogManager _logManager;
            private readonly Filter _filter = new Filter();

            public Builder(ILogManager logManager = null)
            {
                _logManager = logManager;
            }

            public Builder Nonce(Predicate<UInt256> nonce)
            {
                _filter.Nonce = nonce;

                return this;
            }

            public Builder GasPrice(Predicate<UInt256> gasPrice)
            {
                _filter.GasPrice = gasPrice;

                return this;
            }

            public Builder GasLimit(Predicate<long> gasLimit)
            {
                _filter.GasLimit = gasLimit;

                return this;
            }

            public Builder To(Predicate<Address> to)
            {
                _filter.To = to;

                return this;
            }

            public Builder Value(Predicate<UInt256> value)
            {
                _filter.Value = value;

                return this;
            }

            public Builder Data(Predicate<byte[]> data)
            {
                _filter.Data = data;

                return this;
            }

            public Builder Init(Predicate<byte[]> init)
            {
                _filter.Init = init;

                return this;
            }

            public Builder SenderAddress(Predicate<Address> senderAddress)
            {
                _filter.SenderAddress = senderAddress;

                return this;
            }

            public Builder Signature(Predicate<Signature> signature)
            {
                _filter.Signature = signature;

                return this;
            }

            public Builder Hash(Predicate<Keccak> hash)
            {
                _filter.Hash = hash;

                return this;
            }

            public Builder DeliveredBy(Predicate<PublicKey> deliveredBy)
            {
                _filter.DeliveredBy = deliveredBy;

                return this;
            }

            public AcceptWhenTxFilter Build() => new AcceptWhenTxFilter(_filter, _logManager);
        }

        public class Filter
        {
            public Predicate<UInt256> Nonce { get; set; }
            public Predicate<UInt256> GasPrice { get; set; }
            public Predicate<long> GasLimit { get; set; }
            public Predicate<Address> To { get; set; }
            public Predicate<UInt256> Value { get; set; }
            public Predicate<byte[]> Data { get; set; }
            public Predicate<byte[]> Init { get; set; }
            public Predicate<Address> SenderAddress { get; set; }
            public Predicate<Signature> Signature { get; set; }
            public Predicate<Keccak> Hash { get; set; }
            public Predicate<PublicKey> DeliveredBy { get; set; }
        }
    }
}
