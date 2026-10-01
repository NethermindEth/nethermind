// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;

namespace Nethermind.Torrent.Maui;

internal static class Bep46StorageLayout
{
    public static bool SameFeed(Bep46Link left, Bep46Link right)
        => left.PublicKey.AsSpan().SequenceEqual(right.PublicKey) && left.Salt.AsSpan().SequenceEqual(right.Salt);

    public static string VersionDirectory(string downloadRoot, Bep46Link feed, string infoHashHex)
    {
        byte[] target = SHA1.HashData([.. feed.PublicKey, .. feed.Salt]);
        return Path.Combine(downloadRoot, ".bep46", Convert.ToHexString(target).ToLowerInvariant(), infoHashHex);
    }
}
