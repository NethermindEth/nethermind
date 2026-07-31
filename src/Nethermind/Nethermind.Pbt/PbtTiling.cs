// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>Which tiling of the stem trie a database is written in; the shape a whole tree shares.</summary>
/// <remarks>
/// Unlike <see cref="PbtGroupFormat"/>, which two blobs of one tree may differ in, this fixes the
/// keys the tree is stored under: a tree cannot hold blobs of both. It is stamped on the database and
/// checked on the way in. Which of these a node writes is half of its <see cref="PbtTrieLayout"/>.
/// </remarks>
public enum PbtTiling : byte
{
    /// <summary>Six-level independent tiles.</summary>
    SixLevel = 1,

    /// <summary>Eight-level independent tiles.</summary>
    EightLevel = 2,

    /// <summary>Four-level independent tiles.</summary>
    FourLevel = 3,

    /// <summary>Five-level independent tiles.</summary>
    FiveLevel = 4,
}

