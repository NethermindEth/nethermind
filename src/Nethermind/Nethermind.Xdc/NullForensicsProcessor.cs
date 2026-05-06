// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

internal sealed class NullForensicsProcessor : IForensicsProcessor
{
    event EventHandler<ForensicsEvent>? IForensicsProcessor.ForensicsEventEmitted
    {
        add { }
        remove { }
    }

    public Task ForensicsMonitoring(IEnumerable<XdcBlockHeader> headerQcToBeCommitted, QuorumCertificate incomingQC) => Task.CompletedTask;

    public Task ProcessVoteEquivocation(Vote incomingVote) => Task.CompletedTask;

    public Task DetectEquivocationInVotePool(Vote vote, IEnumerable<Vote> votePool) => Task.CompletedTask;
}
