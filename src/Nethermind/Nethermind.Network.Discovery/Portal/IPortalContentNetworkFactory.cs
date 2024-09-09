// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Portal;

public interface IPortalContentNetworkFactory
{
    IPortalContentNetwork Create(ContentNetworkConfig config, IPortalContentNetwork.Store store);
}