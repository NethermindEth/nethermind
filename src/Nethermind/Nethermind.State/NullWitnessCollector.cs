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

        public IReadOnlyCollection<Commitment> Collected => Array.Empty<Commitment>();

        public void Add(Commitment hash)
        {
            throw new InvalidOperationException(
                $"{nameof(NullWitnessCollector)} is not expected to receive {nameof(Add)} calls.");
        }

        public void Reset() { }

        public void Persist(Commitment blockHash) { }

        class EmptyDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }

        public IDisposable TrackOnThisThread() { return new EmptyDisposable(); }

        public Commitment[]? Load(Commitment blockHash)
        {
            throw new InvalidOperationException(
                $"{nameof(NullWitnessCollector)} is not expected to receive {nameof(Load)} calls.");
        }

        public void Delete(Commitment blockHash)
        {
            throw new InvalidOperationException(
                $"{nameof(NullWitnessCollector)} is not expected to receive {nameof(Delete)} calls.");
        }
    }
}
