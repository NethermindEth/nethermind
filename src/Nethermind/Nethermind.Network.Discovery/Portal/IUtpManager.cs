// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;

namespace Nethermind.Network.Discovery.Portal;

public interface IUtpManager
{
    int MaxContentByteSize { get; }
    Task ReadContentFromUtp(IEnr nodeId, bool isInitiator, ushort connectionId, Stream output, CancellationToken token);
    Task WriteContentToUtp(IEnr nodeId, bool isInitiator, ushort connectionId, Stream input, CancellationToken token);
}
