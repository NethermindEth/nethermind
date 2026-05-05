// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

public interface IForensicsProcessor
{
    event EventHandler<ForensicsEvent>? ForensicsEventEmitted;

    Task ForensicsMonitoring(IEnumerable<XdcBlockHeader> headerQcToBeCommitted, QuorumCertificate incomingQC);

    Task ProcessVoteEquivocation(Vote incomingVote);

    Task DetectEquivocationInVotePool(Vote vote, IEnumerable<Vote> votePool);
}
