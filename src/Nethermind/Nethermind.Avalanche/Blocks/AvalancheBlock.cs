// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Avalanche.Blocks;

/// <summary>
/// The Avalanche C-Chain (Coreth) body: transactions, uncles, the Coreth block <c>Version</c>, and the raw
/// atomic-transaction <c>ExtData</c> bytes.
/// </summary>
/// <remarks>
/// Corresponds to the Coreth <c>extblock</c> body fields <c>[Txs, Uncles, Version, ExtData]</c>.
/// <para>
/// <c>ExtData</c> follows Go's <c>rlp:"nil"</c> tag: a <c>nil</c> slice and an empty (zero-length) slice are
/// distinct on the wire. That distinction is preserved here by typing <see cref="ExtData"/> as a nullable
/// byte array — <c>null</c> denotes the Go <c>nil</c> pointer, an empty array denotes a present-but-empty slice.
/// </para>
/// Uncles are always empty on the C-Chain but are modeled for byte-exact round-tripping of the underlying
/// go-ethereum structure.
/// </remarks>
public sealed class AvalancheBlockBody(Transaction[]? transactions, BlockHeader[]? uncles, uint version, byte[]? extData)
{
    public Transaction[] Transactions { get; } = transactions ?? [];

    public BlockHeader[] Uncles { get; } = uncles ?? [];

    /// <summary>The Coreth block <c>Version</c> (a <c>uint32</c>).</summary>
    public uint Version { get; } = version;

    /// <summary>
    /// The raw atomic-transaction bytes. <c>null</c> represents Go's <c>nil</c> slice; an empty array
    /// represents a present-but-empty slice.
    /// </summary>
    public byte[]? ExtData { get; } = extData;
}

/// <summary>
/// An Avalanche C-Chain (Coreth) block: an <see cref="AvalancheBlockHeader"/> plus the Coreth body
/// (<see cref="AvalancheBlockBody"/>).
/// </summary>
/// <remarks>
/// Corresponds to the Coreth <c>extblock</c> structure <c>[Header, Txs, Uncles, Version, ExtData]</c>. Unlike a
/// vanilla Ethereum block, the trailing fields are a <c>Version</c> integer and the <c>ExtData</c> byte string
/// rather than a withdrawals list. The canonical block hash is <c>keccak256(RLP(header))</c> — see
/// <see cref="AvalancheHeaderDecoder.ComputeHash"/>.
/// </remarks>
public sealed class AvalancheBlock(AvalancheBlockHeader header, AvalancheBlockBody body)
{
    public AvalancheBlockHeader Header { get; } = header;

    public AvalancheBlockBody Body { get; } = body;

    public Transaction[] Transactions => Body.Transactions;

    public BlockHeader[] Uncles => Body.Uncles;

    public uint Version => Body.Version;

    public byte[]? ExtData => Body.ExtData;
}
