// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;

namespace Nethermind.Network.Discovery.Portal;

public interface IUtpManager
{
    ushort InitiateUtpStreamSender(IEnr sender, byte[] valuePayload);
    int MaxContentByteSize { get; }
    Task<byte[]?> DownloadContentFromUtp(IEnr node, ushort valueConnectionId, CancellationToken token);
}
