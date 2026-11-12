// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Linq;
using System.Text;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Init;

/// <summary>
/// Replaces <see cref="FlatLayout.Auto"/> with the layout of the flat DB: the one an existing DB was created with, or for
/// a new DB <see cref="FlatLayout.FlatInTrie"/> on a machine with little memory and <see cref="FlatLayout.Flat"/> otherwise.
/// </summary>
/// <remarks>
/// The flat RocksDB options depend on the layout, so it is needed before that DB is opened and cannot be read from its
/// metadata column. The resolved layout is instead recorded in a file in the flat DB directory and read back on later
/// starts, so a change in available memory never switches the layout of a DB. A flat DB with data but without the file
/// predates <see cref="FlatLayout.Auto"/>, when the default was <see cref="FlatLayout.Flat"/>.
/// </remarks>
public sealed class FlatLayoutResolver(IInitConfig initConfig, IHardwareInfo hardwareInfo, IFileSystem fileSystem, ILogManager logManager)
{
    internal const string MarkerFileName = "layout.marker";

    private readonly ILogger _logger = logManager.GetClassLogger<FlatLayoutResolver>();

    public void Resolve(IFlatDbConfig flatDbConfig)
    {
        if (flatDbConfig.Layout != FlatLayout.Auto) return;

        // The flat DB is not opened then, so the layout is not recorded either.
        if (!flatDbConfig.Enabled)
        {
            flatDbConfig.Layout = FlatLayout.Flat;
            return;
        }

        string directory = DbNames.Flat.GetApplicationResourcePath(initConfig.BaseDbPath);
        string markerPath = fileSystem.Path.Combine(directory, MarkerFileName);
        if (fileSystem.File.Exists(markerPath))
        {
            string recorded = fileSystem.File.ReadAllText(markerPath).Trim();
            FlatLayout recordedLayout = recorded switch
            {
                nameof(FlatLayout.Flat) => FlatLayout.Flat,
                nameof(FlatLayout.FlatInTrie) => FlatLayout.FlatInTrie,
                _ => throw new InvalidConfigurationException(
                    $"Unrecognized flat DB layout '{recorded}' in {markerPath}. Set FlatDb.Layout to the layout the flat DB was created with.", -1)
            };

            flatDbConfig.Layout = recordedLayout;
            if (_logger.IsInfo) _logger.Info($"Flat DB layout: {recordedLayout} (recorded in {markerPath}).");
            return;
        }

        (FlatLayout layout, string reason) =
            HasData(directory) ? (FlatLayout.Flat, "existing flat DB")
            : flatDbConfig.HistoryEnabled ? (FlatLayout.Flat, "flat history requires it")
            : hardwareInfo.AvailableMemoryBytes < FlatStateActivationPolicy.LowMemoryLayoutThreshold ? (FlatLayout.FlatInTrie, $"{hardwareInfo.AvailableMemoryBytes / 1.GiB} GB of available memory")
            : (FlatLayout.Flat, $"{hardwareInfo.AvailableMemoryBytes / 1.GiB} GB of available memory");

        fileSystem.Directory.CreateDirectory(directory);
        // Synced aside and renamed into place, so a crash leaves either no marker or a complete one.
        string temporaryPath = markerPath + ".tmp";
        using (FileSystemStream stream = fileSystem.File.Create(temporaryPath))
        {
            stream.Write(Encoding.UTF8.GetBytes(layout.ToString()));
            stream.Flush(flushToDisk: true);
        }
        fileSystem.File.Move(temporaryPath, markerPath);
        flatDbConfig.Layout = layout;
        if (_logger.IsInfo) _logger.Info($"Flat DB layout: {layout} ({reason}).");
    }

    // Until its first flush a DB holds its writes only in its write-ahead log, which an open without writes leaves empty.
    private bool HasData(string directory) =>
        fileSystem.Directory.Exists(directory) &&
        (FlatStateActivationPolicy.HasSstFile(fileSystem, directory)
            || fileSystem.Directory.EnumerateFiles(directory, "*.log").Any(path => fileSystem.FileInfo.New(path).Length > 0));
}
