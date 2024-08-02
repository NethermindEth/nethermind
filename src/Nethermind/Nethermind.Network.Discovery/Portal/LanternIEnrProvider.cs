// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Identity;
using Lantern.Discv5.WireProtocol;

namespace Nethermind.Network.Discovery.Portal;

public class LanternIEnrProvider(
    IDiscv5Protocol discv5,
    IIdentityVerifier identityVerifier,
    IEnrFactory enrFactory
): IEnrProvider
{
    public IEnr Decode(byte[] enrBytes)
    {
        return enrFactory.CreateFromBytes(enrBytes, identityVerifier);
    }

    public IEnr SelfEnr => discv5.SelfEnr;
}
