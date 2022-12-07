// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Databases
{
    public class DepositsInMemoryDb
    {
        private readonly ConcurrentDictionary<Keccak, DepositDetails> _db =
            new ConcurrentDictionary<Keccak, DepositDetails>();

        public DepositDetails? Get(Keccak id) => _db.TryGetValue(id, out DepositDetails? deposit) ? deposit : null;
        public ICollection<DepositDetails> GetAll() => _db.Values;
        public void Add(DepositDetails deposit) => _db.TryAdd(deposit.Id, deposit);
    }
}
