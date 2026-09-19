// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.P2P.Discovery;

/// <summary>The <c>ENRForkID</c> SSZ container advertised in the <c>eth2</c> ENR entry (phase0 p2p spec).</summary>
/// <remarks>
/// Mirrors Lighthouse's <c>enr_fork_id</c> including EIP-7892: <see cref="NextForkEpoch"/> is the next epoch at
/// which the fork digest changes — a scheduled hard fork or, from Fulu onward, also a blob-parameter-only (BPO)
/// fork — while <see cref="NextForkVersion"/> only tracks hard forks (BPO forks do not bump the version) and
/// falls back to the current version when no hard fork is scheduled.
/// </remarks>
public sealed class EnrForkId : IEquatable<EnrForkId>
{
    /// <summary>Fixed SSZ length: fork_digest(4) ++ next_fork_version(4) ++ next_fork_epoch(8).</summary>
    public const int SszLength = 16;

    private const int VersionLength = 4;

    public EnrForkId(byte[] forkDigest, byte[] nextForkVersion, ulong nextForkEpoch)
    {
        if (forkDigest.Length != VersionLength || nextForkVersion.Length != VersionLength)
        {
            throw new ArgumentException($"{nameof(EnrForkId)} digest and version must be {VersionLength} bytes long.");
        }

        ForkDigest = forkDigest;
        NextForkVersion = nextForkVersion;
        NextForkEpoch = nextForkEpoch;
    }

    public byte[] ForkDigest { get; }

    public byte[] NextForkVersion { get; }

    public ulong NextForkEpoch { get; }

    public byte[] Encode()
    {
        byte[] ssz = new byte[SszLength];
        ForkDigest.CopyTo(ssz, 0);
        NextForkVersion.CopyTo(ssz, VersionLength);
        BinaryPrimitives.WriteUInt64LittleEndian(ssz.AsSpan(2 * VersionLength), NextForkEpoch);
        return ssz;
    }

    public static bool TryDecode(ReadOnlySpan<byte> ssz, [NotNullWhen(true)] out EnrForkId? forkId)
    {
        if (ssz.Length != SszLength)
        {
            forkId = null;
            return false;
        }

        forkId = new EnrForkId(
            ssz[..VersionLength].ToArray(),
            ssz[VersionLength..(2 * VersionLength)].ToArray(),
            BinaryPrimitives.ReadUInt64LittleEndian(ssz[(2 * VersionLength)..]));
        return true;
    }

    /// <summary>Computes the <c>ENRForkID</c> to advertise at the given epoch.</summary>
    public static EnrForkId Compute(BeaconChainSpec spec, ulong epoch)
    {
        byte[] nextForkVersion = spec.VersionForEpoch(epoch);
        foreach (ForkScheduleEntry fork in spec.Forks)
        {
            if (fork.Epoch > epoch)
            {
                nextForkVersion = fork.Version;
                break;
            }
        }

        return new EnrForkId(Spec.ForkDigest.Compute(spec, epoch), nextForkVersion, NextDigestEpoch(spec, epoch));
    }

    /// <summary>
    /// Returns the next epoch after <paramref name="epoch"/> at which the fork digest changes, or
    /// <see cref="Presets.FarFutureEpoch"/> when none is scheduled.
    /// </summary>
    /// <remarks>
    /// Before Fulu only hard forks rotate the digest; from Fulu onward EIP-7892 BPO forks rotate it too,
    /// matching Lighthouse's <c>next_digest_epoch</c>.
    /// </remarks>
    public static ulong NextDigestEpoch(BeaconChainSpec spec, ulong epoch)
    {
        ulong next = Presets.FarFutureEpoch;
        foreach (ForkScheduleEntry fork in spec.Forks)
        {
            if (fork.Epoch > epoch && fork.Epoch < next)
            {
                next = fork.Epoch;
            }
        }

        if (epoch >= spec.FuluForkEpoch)
        {
            foreach (BlobScheduleEntry bpo in spec.BlobSchedule)
            {
                if (bpo.Epoch > epoch && bpo.Epoch < next)
                {
                    next = bpo.Epoch;
                }
            }
        }

        return next;
    }

    public bool Equals(EnrForkId? other) =>
        other is not null &&
        NextForkEpoch == other.NextForkEpoch &&
        Bytes.AreEqual(ForkDigest, other.ForkDigest) &&
        Bytes.AreEqual(NextForkVersion, other.NextForkVersion);

    public override bool Equals(object? obj) => Equals(obj as EnrForkId);

    public override int GetHashCode() => HashCode.Combine(BinaryPrimitives.ReadUInt32LittleEndian(ForkDigest), NextForkEpoch);

    public override string ToString() => $"fork_digest: {ForkDigest.ToHexString()}, next_fork_version: {NextForkVersion.ToHexString()}, next_fork_epoch: {NextForkEpoch}";
}
