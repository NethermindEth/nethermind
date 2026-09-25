// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Image;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Keeps the exact native anchor snapshot and bootstrap inputs alive through publication.</summary>
internal sealed class RuntimeBootstrapLease : PbtBootstrapLease
{
    private readonly List<IDisposable> _owned = [];
    private readonly Func<bool> _isCurrent;
    private Stream? _snapshot;
    private Stream? _preimages;
    private IPersistence.IPersistenceReader? _source;
    private IReadOnlyKeyValueStore? _code;

    private RuntimeBootstrapLease(PbtImageAnchor anchor, IPersistence flatPersistence,
        string scratchDirectory, Func<bool> isCurrent)
    {
        Anchor = anchor;
        MptAnchor = flatPersistence.CreateReader();
        _owned.Add(MptAnchor);
        ScratchDirectory = scratchDirectory;
        _isCurrent = isCurrent;
    }

    public static RuntimeBootstrapLease Create(PbtImageAnchor anchor, IPersistence flatPersistence,
        string scratchDirectory, Func<bool> isCurrent, IPbtConfig configuration,
        MigrationGenesisSource? genesisSource, IReadOnlyKeyValueStore code, ILogManager logManager)
    {
        RuntimeBootstrapLease lease = new(anchor, flatPersistence, scratchDirectory, isCurrent);
        try
        {
            if (configuration.MigrationSnapshotPath is { } snapshot)
            {
                lease._snapshot = File.Open(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read);
                lease._owned.Add(lease._snapshot);
                lease._preimages = File.Open(configuration.MigrationPreimagesPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
                lease._owned.Add(lease._preimages);
            }
            else if (configuration.MigrationPreimageSourcePath is { } sourcePath)
            {
                MigrationOfflineSourceLease source = MigrationOfflineSourceLease.Open(sourcePath, logManager);
                lease._owned.Add(source);
                lease._source = source.Reader;
                lease._code = source.Code;
            }
            else if (configuration.MigrationGenesisBootstrap)
            {
                lease._source = (genesisSource ?? throw new InvalidOperationException("Genesis bootstrap source is not registered.")).Persistence.CreateReader();
                lease._owned.Add(lease._source);
                lease._code = code;
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public override PbtImageAnchor Anchor { get; }
    public override IPersistence.IPersistenceReader MptAnchor { get; }
    public override string ScratchDirectory { get; }
    public override Stream? Snapshot => _snapshot;
    public override Stream? Preimages => _preimages;
    public override IPersistence.IPersistenceReader? OfflineSource => _source;
    public override IReadOnlyKeyValueStore? OfflineCode => _code;

    public override bool IsAnchorCurrent() => _isCurrent();

    public override void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _owned.Clear();
    }
}
