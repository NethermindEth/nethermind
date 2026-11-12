// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Eez.Follower;

/// <summary>Restores the state of an L1-authenticated local checkpoint before the follower publishes it.</summary>
public interface IFollowerStateRecovery
{
    /// <summary>Completes when checkpoint state is committed, without changing the forkchoice.</summary>
    /// <exception cref="EezEngineUnavailableException">Recovery made bounded progress or processing needs a retry.</exception>
    /// <exception cref="L1SourceIncompleteException">Canonical L1 evidence is unavailable or changed.</exception>
    /// <exception cref="EezFollowerException">A stored block fails validation or no retained ancestor state exists.</exception>
    Task Restore(BlockHeader endpoint, CancellationToken token);
}
