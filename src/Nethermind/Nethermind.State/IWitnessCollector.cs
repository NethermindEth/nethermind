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
        IReadOnlyCollection<Commitment> Collected { get; }

        void Add(Commitment hash);

        void Reset();

        void Persist(Commitment blockHash);

        IDisposable TrackOnThisThread();
    }
}
