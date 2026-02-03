// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity;
using NBitcoin.Secp256k1;
using Nethermind.Config;

namespace Nethermind.Network.Discovery.Discv5;

public static class NetworkNodeExtensions
{
    public static Lantern.Discv5.Enr.Enr ToEnr(this NetworkNode node, IIdentityVerifier verifier, IIdentitySigner signer)
    {
        if (node.IsEnr) return node.Enr;

        Enode enode = node.Enode;
        return new EnrBuilder()
            .WithIdentityScheme(verifier, signer)
            .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
            .WithEntry(EnrEntryKey.Ip, new EntryIp(enode.HostIp))
            .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(Context.Instance.CreatePubKey(enode.PublicKey.PrefixedBytes).ToBytes(false)))
            .WithEntry(EnrEntryKey.Tcp, new EntryTcp(enode.Port))
            .WithEntry(EnrEntryKey.Udp, new EntryUdp(enode.DiscoveryPort))
            .Build();
    }
}
