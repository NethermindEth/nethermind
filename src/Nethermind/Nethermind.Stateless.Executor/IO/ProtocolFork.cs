// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.Forks;

namespace Nethermind.Stateless.Execution.IO;

/// <summary>
/// Stable execution-layer fork identifiers used as the first byte of the stateless input schema id,
/// as defined by the execution specs <c>ProtocolFork</c> enum.
/// </summary>
/// <remarks>
/// Only forks whose payloads are representable by the stateless input schemas are listed:
/// Cancun through BPO2 use the pre-BAL payload, Amsterdam adds the EIP-7928/EIP-7843 fields.
/// </remarks>
public enum ProtocolFork : byte
{
    Cancun = 0x10,
    Prague = 0x11,
    Osaka = 0x12,
    BPO1 = 0x13,
    BPO2 = 0x14,
    Amsterdam = 0x15
}

public static class ProtocolForkExtensions
{
    private static readonly Dictionary<string, ProtocolFork> _forksByName = new(StringComparer.Ordinal)
    {
        [Cancun.Instance.Name] = ProtocolFork.Cancun,
        [Prague.Instance.Name] = ProtocolFork.Prague,
        [Osaka.Instance.Name] = ProtocolFork.Osaka,
        [BPO1.Instance.Name] = ProtocolFork.BPO1,
        [BPO2.Instance.Name] = ProtocolFork.BPO2,
        [Amsterdam.Instance.Name] = ProtocolFork.Amsterdam
    };

    public static bool TryGetByName(string name, out ProtocolFork fork) => _forksByName.TryGetValue(name, out fork);

    public static string GetName(this ProtocolFork fork) => fork switch
    {
        ProtocolFork.Cancun => Cancun.Instance.Name,
        ProtocolFork.Prague => Prague.Instance.Name,
        ProtocolFork.Osaka => Osaka.Instance.Name,
        ProtocolFork.BPO1 => BPO1.Instance.Name,
        ProtocolFork.BPO2 => BPO2.Instance.Name,
        ProtocolFork.Amsterdam => Amsterdam.Instance.Name,
        _ => throw new ArgumentOutOfRangeException(nameof(fork), fork, "Unknown protocol fork")
    };

    /// <summary>Composes the schema id selecting the SSZ <c>StatelessInput</c> encoding (revision 1) for the fork.</summary>
    public static ushort ToRevision1SchemaId(this ProtocolFork fork) =>
        (ushort)(((ushort)fork << 8) | InputDecoder.Revision1);
}
