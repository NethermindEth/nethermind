// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.State
{
    /// <summary>
    /// Collects witnesses during block processing, allows to persist them
    /// </summary>
    public interface IWitnessCollector
    {
        IReadOnlyCollection<Keccak> Collected { get; }

        void Add(Keccak hash);

        void Reset();

        void Persist(Keccak blockHash);

        IDisposable TrackOnThisThread();
    }
}
