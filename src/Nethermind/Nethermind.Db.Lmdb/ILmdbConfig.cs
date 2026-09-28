// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Db.Lmdb;

/// <summary>
/// Configuration for the experimental LMDB-backed FlatDb Account/Storage columns plugin.
/// </summary>
[ConfigCategory(Description = "Configuration for the experimental LMDB-backed FlatDb Account/Storage columns.")]
public interface ILmdbConfig : IConfig
{
    /// <summary>
    /// Gets or sets a value indicating whether the FlatDb Account/Storage columns are backed by LMDB instead of
    /// RocksDB. This is a learning/benchmarking spike, not a production backend - see PLAN-LMDB.md.
    /// </summary>
    [ConfigItem(Description = "Whether the FlatDb Account/Storage columns are backed by LMDB instead of RocksDB.", DefaultValue = "false")]
    bool Enabled { get; set; }

    /// <summary>
    /// The initial <c>MapSize</c> (in bytes) reserved for each of the Account and Storage LMDB environments.
    /// LMDB requires an upfront maximum-size reservation; sparse files mean this does not cost real disk until
    /// pages are actually written, so it should be set generously for the dataset a validation run expects to
    /// reach (see PLAN-LMDB.md "MapSize sizing and growth"). If a write ever exceeds this, the map is doubled and
    /// the write retried automatically - see <see cref="LmdbDb"/>.
    /// </summary>
    [ConfigItem(Description = "Initial MapSize, in bytes, reserved for each of the Account and Storage LMDB environments.", DefaultValue = "68719476736")]
    long InitialMapSizeBytes { get; set; }
}
