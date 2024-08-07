// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.WireProtocol.Session;
using Nethermind.Crypto;

namespace Nethermind.Network.Discovery.Test.Portal;

internal class TestUtils
{
    public static IEnr CreateEnr(PrivateKey privateKey)
    {
        SessionOptions sessionOptions = new SessionOptions
        {
            Signer = new IdentitySignerV4(privateKey.KeyBytes),
            Verifier = new IdentityVerifierV4(),
            SessionKeys = new SessionKeys(privateKey.KeyBytes),
        };

        return new EnrBuilder()
            .WithIdentityScheme(sessionOptions.Verifier, sessionOptions.Signer)
            .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
            .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(
                NBitcoin.Secp256k1.Context.Instance.CreatePubKey(privateKey.PublicKey.PrefixedBytes).ToBytes(false)
            ))
            .Build();
    }
}
