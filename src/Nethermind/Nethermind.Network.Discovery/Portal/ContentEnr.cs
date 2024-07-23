// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;

namespace Nethermind.Network.Discovery.Portal;

public class ContentEnr(byte[] nodeId): IEnr
{
    public string ToPeerId()
    {
        throw new NotImplementedException();
    }

    public string ToEnode()
    {
        throw new NotImplementedException();
    }

    public bool HasKey(string key)
    {
        throw new NotImplementedException();
    }

    public void UpdateEntry<T>(T value) where T : class, IEntry
    {
        throw new NotImplementedException();
    }

    public T GetEntry<T>(string key, T defaultValue) where T : IEntry
    {
        throw new NotImplementedException();
    }

    public byte[] EncodeRecord()
    {
        throw new NotImplementedException();
    }

    public byte[] EncodeContent()
    {
        throw new NotImplementedException();
    }

    public void UpdateSignature()
    {
        throw new NotImplementedException();
    }

    public byte[]? Signature { get; }
    public ulong SequenceNumber { get; }
    public byte[] NodeId { get; } = nodeId;
}
