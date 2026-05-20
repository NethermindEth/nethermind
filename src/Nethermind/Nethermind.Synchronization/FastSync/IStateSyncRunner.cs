// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Synchronization.FastSync;

public interface IStateSyncRunner
{
    Task Run(CancellationToken token);

    Task RunStateSyncRounds(CancellationToken token);
}
