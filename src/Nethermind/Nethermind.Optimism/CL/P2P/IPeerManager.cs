// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Multiformats.Address;

namespace Nethermind.Optimism.CL.P2P;

public interface IPeerManager
{
    IEnumerable<Multiaddress> GetPeers();
    void AddActivePeer(Multiaddress peer);
    void AddInactivePeer(Multiaddress peer);
    void IncreaseRating(Multiaddress peer);
    void DecreaseRating(Multiaddress peer);
}
