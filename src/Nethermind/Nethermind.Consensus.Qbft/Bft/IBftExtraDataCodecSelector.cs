// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Qbft.Bft;

/// <summary>Chooses the extra data codec that applies at a block height.</summary>
/// <remarks>
/// A chain that migrated from IBFT 2.0 keeps IBFT-encoded headers below the QBFT
/// <c>startBlock</c>, so codec selection is per height rather than per chain.
/// </remarks>
public interface IBftExtraDataCodecSelector
{
    IBftExtraDataCodec ForBlock(ulong blockNumber);
}

/// <summary>Every block uses the QBFT codec.</summary>
public sealed class QbftOnlyCodecSelector : IBftExtraDataCodecSelector
{
    public static readonly QbftOnlyCodecSelector Instance = new();

    public IBftExtraDataCodec ForBlock(ulong blockNumber) => QbftExtraDataCodec.Instance;
}

/// <summary>Every block of an IBFT 2.0 chain.</summary>
public sealed class Ibft2OnlyCodecSelector : IBftExtraDataCodecSelector
{
    public static readonly Ibft2OnlyCodecSelector Instance = new();

    public IBftExtraDataCodec ForBlock(ulong blockNumber) => Ibft2ExtraDataCodec.Instance;
}

/// <summary>Blocks below <paramref name="qbftStartBlock"/> use the IBFT 2.0 codec, the rest the QBFT codec.</summary>
public sealed class MigrationCodecSelector(ulong qbftStartBlock) : IBftExtraDataCodecSelector
{
    public ulong QbftStartBlock { get; } = qbftStartBlock;

    public IBftExtraDataCodec ForBlock(ulong blockNumber) =>
        blockNumber < QbftStartBlock ? Ibft2ExtraDataCodec.Instance : QbftExtraDataCodec.Instance;
}
