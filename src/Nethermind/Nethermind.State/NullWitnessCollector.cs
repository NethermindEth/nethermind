// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.State
{
    public class NullWitnessCollector : IWitnessCollector, IWitnessRepository
    {
        private NullWitnessCollector() { }

        public static NullWitnessCollector Instance { get; } = new();

        public IReadOnlyCollection<Hash256> Collected => Array.Empty<Hash256>();

        public void Add(Hash256 hash)
        {
            throw new InvalidOperationException(
                $"{nameof(NullWitnessCollector)} is not expected to receive {nameof(Add)} calls.");
        }

        public void Reset() { }

        public void Persist(Hash256 blockHash) { }

        class EmptyDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }

        public IDisposable TrackOnThisThread() { return new EmptyDisposable(); }

        public Hash256[]? Load(Hash256 blockHash)
        {
            throw new InvalidOperationException(
                $"{nameof(NullWitnessCollector)} is not expected to receive {nameof(Load)} calls.");
        }

        public void Delete(Hash256 blockHash)
        {
            throw new InvalidOperationException(
                $"{nameof(NullWitnessCollector)} is not expected to receive {nameof(Delete)} calls.");
        }
    }
}
