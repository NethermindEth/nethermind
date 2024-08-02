// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Portal.Messages;

public class FindContent
{
    public byte[] ContentKey { get; set; } = Array.Empty<byte>();
}
