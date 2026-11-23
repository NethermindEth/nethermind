// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Messages;

/// <summary>Marks a request that the transaction pool serves, not the sync data.</summary>
/// <remarks>
/// <see cref="Utils.BackgroundTaskSchedulerWrapper"/> counts these requests in a separate per-peer in-flight counter,
/// so that they do not use up the slots of the sync requests.
/// </remarks>
public interface ITxServeRequest;
