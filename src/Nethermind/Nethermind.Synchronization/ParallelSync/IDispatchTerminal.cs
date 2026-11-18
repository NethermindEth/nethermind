// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Synchronization.ParallelSync;

internal interface IDispatchTerminal
{
    void PrepareDispatch(Peers.ISyncPeerPool peerPool);
    void CompleteDispatch();
}
