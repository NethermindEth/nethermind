// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Ssz;

namespace Nethermind.Network.Portal.Messages;

[SszSerializable]
public class FindContent
{
    public ContentKey ContentKey { get; set; } = null!;
}
