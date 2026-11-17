// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

public enum FlatLayout
{
    Flat,
    FlatInTrie,
    PreimageFlatV1,
    PreimageFlat,

    /// <summary>Resolved to a concrete layout at startup and never stored in the flat DB metadata.</summary>
    /// <remarks>Kept last: the metadata stores a layout as its numeric value.</remarks>
    Auto,
}
