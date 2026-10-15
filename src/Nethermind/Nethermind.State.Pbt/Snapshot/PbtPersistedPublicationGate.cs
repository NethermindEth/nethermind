// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.State.Pbt.Snapshot;

public sealed class PbtPersistedPublicationGate
{
    public Lock Sync { get; } = new();
}
