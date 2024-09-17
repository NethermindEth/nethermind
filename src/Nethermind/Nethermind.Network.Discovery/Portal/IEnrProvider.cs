// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Org.BouncyCastle.Utilities.Encoders;

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// Interface that simplify getting and decoding enr
/// Useful for unit testing simplicity
/// </summary>
public interface IEnrProvider
{
    IEnr Decode(string enrStr);

    IEnr Decode(byte[] enrBytes);

    IEnr SelfEnr { get; }
}
