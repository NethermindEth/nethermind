// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Portal.Messages;

public class Offer
{
    public byte[][] ContentKeys { get; set; } = Array.Empty<byte[]>();
}
