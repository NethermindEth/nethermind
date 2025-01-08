// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Network.Portal.History;

[SszSerializable]
public class PortalBlockHeaderWithProof
{
    [SszList(2048)]
    public byte[] Header { get; set; } = [];

    public PortalBlockHeaderProof Proof { get; set; } = PortalBlockHeaderProof.Default;
}

[SszSerializable]
public class PortalBlockHeaderProof
{
    public static PortalBlockHeaderProof Default { get; } = new PortalBlockHeaderProof { Selector = PortalBlockHeaderProofSelector.None };
    public PortalBlockHeaderProofSelector Selector { get; set; }

    [SszVector(15)] public ValueHash256[]? AccumulatorProof { get; set; }
    public BlockProofHistoricalRoots? BlockProofHistoricalRoots { get; set; }
    [SszVector(13)] public ValueHash256[]? BlockProofHistoricalSummaries { get; set; }
}

[SszSerializable]
public class BlockProofHistoricalRoots
{
    [SszVector(14)]
    public ValueHash256[]? BeaconBlockProof { get; set; } = [];
    public ValueHash256 BeaconBlockRoot { get; set; }

    [SszVector(11)]
    public ValueHash256[]? ExecutionBlockProof { get; set; } = [];
    public ulong Slot { get; set; }
}

public enum PortalBlockHeaderProofSelector
{
    None = 0,
    AccumulatorProof = 1,
    BlockProofHistoricalRoots = 2,
    BlockProofHistoricalSummaries = 3
}

[SszSerializable]
public class PortalBlockBodyPreShanghai
{
    [SszList(2 << 14)]
    public SszTransaction[] Transactions { get; set; } = [];

    [SszList(2 << 17)]
    public byte[] Uncles { get; set; } = [];
}

[SszSerializable]
public class PortalBlockBodyPostShanghai
{
    [SszList(2 << 14)]
    public SszTransaction[] Transactions { get; set; } = [];

    [SszList(2 << 17)]
    public byte[] Uncles { get; set; } = [];

    [SszList(16)]
    public EncodedWidthrawals[] Withdrawals { get; set; } = [];
}

[SszSerializable(isCollectionItself: true)]
public class SszTransaction
{
    [SszList(2 << 24)]
    public byte[] Data { get; set; } = [];
}


[SszSerializable(isCollectionItself: true)]
public class SszReceipt
{
    [SszList(2 << 24)]
    public byte[] Data { get; set; } = [];
}

[SszSerializable(isCollectionItself: true)]
public class EncodedWidthrawals
{
    [SszList(64)]
    public byte[] Data { get; set; } = [];
}

[SszSerializable]
public class PortalReceiptsSSZ
{
    [SszList(2 << 14)]
    public byte[]? AsBytes { get; set; }
}

[SszSerializable(isCollectionItself: true)]
public class EncodedReceipts
{
    [SszList(2 << 27)]
    public byte[] Data { get; set; } = [];
}
