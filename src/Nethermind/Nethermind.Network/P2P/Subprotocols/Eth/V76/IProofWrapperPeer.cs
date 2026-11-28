// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V76;

/// <summary>An <c>eth/76</c> connection as <see cref="ProofWrapperRelay"/> uses it.</summary>
internal interface IProofWrapperPeer
{
    void Announce(IReadOnlyList<WrapperAnnouncement> announcements);

    Task<IReadOnlyList<ServedWrapper>> GetProofWrappers(IReadOnlyList<ValueHash256> hashes, bool compact, CancellationToken token);

    Task<IReadOnlyList<ServedManifest>> GetProofWrapperManifests(IReadOnlyList<ValueHash256> hashes, CancellationToken token);

    void Disconnect(string reason);
}
