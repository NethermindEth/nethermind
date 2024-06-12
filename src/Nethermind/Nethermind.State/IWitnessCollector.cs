// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
        IReadOnlyCollection<Hash256> Collected { get; }

        void Add(Hash256 hash);

        void Reset();

        void Persist(Hash256 blockHash);

        IDisposable TrackOnThisThread();
    }
}
