using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.TransactionPools.Filters
{
    public class AcceptWhenTransactionFilter : ITransactionFilter
    {
        private readonly Builder.Filter _addFilter;
        private readonly Builder.Filter _deleteFilter;

        private AcceptWhenTransactionFilter(Builder.Filter addFilter, Builder.Filter deleteFilter)
        {
            _addFilter = addFilter;
            _deleteFilter = deleteFilter;
        }

        public bool CanAdd(Transaction transaction) => Valid(transaction, _addFilter);
        public bool CanDelete(Transaction transaction) => Valid(transaction, _deleteFilter);

        private static bool Valid(Transaction transaction, Builder.Filter filter)
        {
            if (filter.Nonce != null && !filter.Nonce(transaction.Nonce))
            {
                return false;
            }

            if (filter.GasPrice != null && !filter.GasPrice(transaction.GasPrice))
            {
                return false;
            }

            if (filter.GasLimit != null && !filter.GasLimit(transaction.GasLimit))
            {
                return false;
            }

            if (filter.Hash != null && !filter.Hash(transaction.Hash))
            {
                return false;
            }

            if (filter.DeliveredBy != null && !filter.DeliveredBy(transaction.DeliveredBy))
            {
                return false;
            }

            if (filter.To != null && !filter.To(transaction.To))
            {
                return false;
            }

            if (filter.Value != null && !filter.Value(transaction.Value))
            {
                return false;
            }

            if (filter.Data != null && !filter.Data(transaction.Data))
            {
                return false;
            }

            if (filter.Init != null && !filter.Init(transaction.Init))
            {
                return false;
            }

            if (filter.SenderAddress != null && !filter.SenderAddress(transaction.SenderAddress))
            {
                return false;
            }

            if (filter.Signature != null && !filter.Signature(transaction.Signature))
            {
                return false;
            }

            return true;
        }

        public static Builder Create() => new Builder();

        public class Builder
        {
            private readonly Filter _addFilter = new Filter();
            private readonly Filter _deleteFilter = new Filter();

            public FilterBuilder AddWhen() => new FilterBuilder(this, _addFilter);
            public FilterBuilder DeleteWhen() => new FilterBuilder(this, _deleteFilter);

            public AcceptWhenTransactionFilter BuildFilter() =>
                new AcceptWhenTransactionFilter(_addFilter, _deleteFilter);

            public class FilterBuilder
            {
                private readonly Builder _builder;
                private readonly Filter _filter;

                public FilterBuilder(Builder builder, Filter filter)
                {
                    _builder = builder;
                    _filter = filter;
                }

                public FilterBuilder Nonce(Predicate<UInt256> nonce)
                {
                    _filter.Nonce = nonce;

                    return this;
                }

                public FilterBuilder GasPrice(Predicate<UInt256> gasPrice)
                {
                    _filter.GasPrice = gasPrice;

                    return this;
                }

                public FilterBuilder GasLimit(Predicate<UInt256> gasLimit)
                {
                    _filter.GasLimit = gasLimit;

                    return this;
                }

                public FilterBuilder To(Predicate<Address> to)
                {
                    _filter.To = to;

                    return this;
                }

                public FilterBuilder Value(Predicate<UInt256> value)
                {
                    _filter.Value = value;

                    return this;
                }

                public FilterBuilder Data(Predicate<byte[]> data)
                {
                    _filter.Data = data;

                    return this;
                }

                public FilterBuilder Init(Predicate<byte[]> init)
                {
                    _filter.Init = init;

                    return this;
                }

                public FilterBuilder SenderAddress(Predicate<Address> senderAddress)
                {
                    _filter.SenderAddress = senderAddress;

                    return this;
                }

                public FilterBuilder Signature(Predicate<Signature> signature)
                {
                    _filter.Signature = signature;

                    return this;
                }

                public FilterBuilder Hash(Predicate<Keccak> hash)
                {
                    _filter.Hash = hash;

                    return this;
                }

                public FilterBuilder DeliveredBy(Predicate<PublicKey> deliveredBy)
                {
                    _filter.DeliveredBy = deliveredBy;

                    return this;
                }

                public Builder Build() => _builder;
            }

            public class Filter
            {
                public Predicate<UInt256> Nonce { get; set; }
                public Predicate<UInt256> GasPrice { get; set; }
                public Predicate<UInt256> GasLimit { get; set; }
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
}
