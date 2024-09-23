// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Network.Discovery.Portal.History;

[SszSerializable]
public class PortalBlockHeaderWithProof
{
    [SszList(2048)]
    public byte[] Header { get; set; } = Array.Empty<byte>();

    [SszList(2048)]
    public byte[] Proof { get; set; } = Array.Empty<byte>();
}

public class PortalBlockHeaderProof
{
    public PortalBlockHeaderProofSelector Selector { get; set; }

    [SszList(16)] public ValueHash256[] Accumulator { get; set; } = Array.Empty<ValueHash256>();
}

public enum PortalBlockHeaderProofSelector
{
    None = 0,
    AccumulatorProof = 1,
}

[SszSerializable]
public class PortalBlockBodyPreShanghai
{
    [SszList(2<<14)]
    public SszTransaction[] Transactions { get; set; } = Array.Empty<SszTransaction>();

    [SszList(2<<17)]
    public byte[] Uncles { get; set; } = Array.Empty<byte>();
}

[SszSerializable]
public class PortalBlockBodyPostShanghai
{
    [SszList(2<<14)]
    public SszTransaction[] Transactions { get; set; } = Array.Empty<SszTransaction>();

    [SszList(2<<17)]
    public byte[] Uncles { get; set; } = Array.Empty<byte>();

    [SszList(16)]
    public EncodedWidthrawals[] Withdrawals { get; set; } = Array.Empty<EncodedWidthrawals>();
}

[SszSerializable(isCollectionItself: true)]
public class SszTransaction
{
    [SszList(2<<24)]
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

[SszSerializable(isCollectionItself: true)]
public class EncodedWidthrawals
{
    [SszList(64)]
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

[SszSerializable]
public class PortalReceiptsSSZ
{
    [SszList(2<<14)]
    public byte[]? AsBytes { get; set; }
}

[SszSerializable(isCollectionItself: true)]
public class EncodedReceipts
{
    [SszList(2<<27)]
    public byte[] Data { get; set; } = Array.Empty<byte>();
}
