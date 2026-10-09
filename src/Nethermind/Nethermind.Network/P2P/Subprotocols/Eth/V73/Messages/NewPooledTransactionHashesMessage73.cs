// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;

/// <summary>EIP-8077 announcement: eth/72's plus each transaction's source and nonce.</summary>
public class NewPooledTransactionHashesMessage73(
    IOwnedReadOnlyList<byte> types,
    IOwnedReadOnlyList<int> sizes,
    IOwnedReadOnlyList<ValueHash256> hashes,
    byte[] cellMask,
    IOwnedReadOnlyList<Address> sources,
    IOwnedReadOnlyList<ulong> nonces)
    : NewPooledTransactionHashesMessage72(types, sizes, hashes, cellMask)
{
    public NewPooledTransactionHashesMessage73(byte[] types, int[] sizes, ValueHash256[] hashes, byte[] cellMask, Address[] sources, ulong[] nonces)
        : this(types.ToPooledList(), sizes.ToPooledList(), hashes.ToPooledList(), cellMask, sources.ToPooledList(), nonces.ToPooledList())
    {
    }

    public IOwnedReadOnlyList<Address> Sources { get; } = sources;
    public IOwnedReadOnlyList<ulong> Nonces { get; } = nonces;

    public override string ToString() => $"{nameof(NewPooledTransactionHashesMessage73)}({Hashes.Count})";

    public override void Dispose()
    {
        base.Dispose();
        Sources.Dispose();
        Nonces.Dispose();
    }
}
