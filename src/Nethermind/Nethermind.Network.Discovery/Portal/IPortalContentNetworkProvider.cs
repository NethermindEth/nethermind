// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Portal;

public interface IPortalContentNetworkProvider
{
    IPortalContentNetwork Create(byte[] networkId, IPortalContentNetwork.Store store);
}
