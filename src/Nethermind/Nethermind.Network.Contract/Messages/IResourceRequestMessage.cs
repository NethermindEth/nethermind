// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Contract.Messages;

public interface IResourceRequestMessage<out TMessage, in TResourceId>
{
    static abstract TMessage From(TResourceId resourceId);
}
