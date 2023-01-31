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

        public IReadOnlyCollection<Keccak> Collected => Array.Empty<Keccak>();

        public void Add(Keccak hash)
        {
            throw new InvalidOperationException(
                $"{nameof(NullWitnessCollector)} is not expected to receive {nameof(Add)} calls.");
        }

        public void Reset() { }

        public void Persist(Keccak blockHash) { }

        class EmptyDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }

        public IDisposable TrackOnThisThread() { return new EmptyDisposable(); }

        public Keccak[]? Load(Keccak blockHash)
        {
            throw new InvalidOperationException(
                $"{nameof(NullWitnessCollector)} is not expected to receive {nameof(Load)} calls.");
        }

        public void Delete(Keccak blockHash)
        {
            throw new InvalidOperationException(
                $"{nameof(NullWitnessCollector)} is not expected to receive {nameof(Delete)} calls.");
        }
    }
}
