// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using Nethermind.Network.StaticNodes;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test;

/// <summary>Persistence contract shared by <see cref="StaticNodesManager"/> and <see cref="TrustedNodesManager"/>: the nodes file mirrors only the peers explicitly persisted.</summary>
[Parallelizable(ParallelScope.Self)]
public abstract class NodesManagerPersistenceTests<TManager>
{
    private const string EnodeA =
        "enode://94c15d1b9e2fe7ce56e458b9a3b672ef11894ddedd0c6f247e0f1d3487f52b66208fb4aeb8179fce6e3a749ea93ed147c37976d67af557508d199d9594c35f09@192.81.208.223:30303";

    private const string EnodeB =
        "enode://94c15d1b9e2fe7ce56e458b9a3b672ef11894ddedd0c6f247e0f1d3487f52b66208fb4aeb8179fce6e3a749ea93ed147c37976d67af557508d199d9594c35f0a@192.81.208.223:30304";

    protected abstract TManager CreateManager(string path);
    protected abstract Task InitAsync(TManager manager);
    protected abstract Task<bool> AddAsync(TManager manager, string enode, bool updateFile);
    protected abstract Task<bool> RemoveAsync(TManager manager, string enode, bool updateFile);

    [Test]
    public async Task PersistentAdd_OfNodePreviouslyAddedInMemoryOnly_WritesItToFile()
    {
        using TempPath tempPath = TempPath.GetTempFile();
        TManager manager = await CreateManagerWithLoadedFile(tempPath);

        await AddAsync(manager, EnodeA, updateFile: false);
        await AddAsync(manager, EnodeA, updateFile: true);

        Assert.That(await File.ReadAllTextAsync(tempPath.Path), Does.Contain(PublicKeyOf(EnodeA)), "upgrading an in-memory node to persistent must write the file");
    }

    [Test]
    public async Task PersistentRemove_OfNodePreviouslyRemovedInMemoryOnly_ScrubsItFromFile()
    {
        using TempPath tempPath = TempPath.GetTempFile();
        TManager manager = await CreateManagerWithLoadedFile(tempPath);
        await AddAsync(manager, EnodeA, updateFile: true);

        await RemoveAsync(manager, EnodeA, updateFile: false);
        await RemoveAsync(manager, EnodeA, updateFile: true);

        Assert.That(await File.ReadAllTextAsync(tempPath.Path), Does.Not.Contain(PublicKeyOf(EnodeA)), "a persistent remove of an already-forgotten node must still scrub the file");
    }

    [Test]
    public async Task PersistentAdd_DoesNotPersistUnrelatedInMemoryOnlyNodes()
    {
        using TempPath tempPath = TempPath.GetTempFile();
        TManager manager = await CreateManagerWithLoadedFile(tempPath);

        await AddAsync(manager, EnodeB, updateFile: false);
        await AddAsync(manager, EnodeA, updateFile: true);

        string file = await File.ReadAllTextAsync(tempPath.Path);
        Assert.That(file, Does.Contain(PublicKeyOf(EnodeA)), "the promoted node must be persisted");
        Assert.That(file, Does.Not.Contain(PublicKeyOf(EnodeB)), "a persistent add must not commit unrelated peers added with persistent=false");
    }

    [Test]
    public async Task PersistentRemove_DoesNotDropUnrelatedNodesRemovedInMemoryOnly()
    {
        using TempPath tempPath = TempPath.GetTempFile();
        TManager manager = await CreateManagerWithLoadedFile(tempPath);
        await AddAsync(manager, EnodeA, updateFile: true);
        await AddAsync(manager, EnodeB, updateFile: true);

        await RemoveAsync(manager, EnodeB, updateFile: false);
        await RemoveAsync(manager, EnodeA, updateFile: true);

        string file = await File.ReadAllTextAsync(tempPath.Path);
        Assert.That(file, Does.Not.Contain(PublicKeyOf(EnodeA)), "the persistently removed node must be scrubbed");
        Assert.That(file, Does.Contain(PublicKeyOf(EnodeB)), "a persistent remove must not drop unrelated peers removed with persistent=false");
    }

    [Test]
    public async Task NoOpRemove_WhenNodesWereNeverLoaded_DoesNotRewriteFile()
    {
        using TempPath tempPath = TempPath.GetTempFile();
        string original = $"[\"{EnodeA}\"]";
        await File.WriteAllTextAsync(tempPath.Path, original);
        TManager manager = CreateManager(tempPath.Path);

        await RemoveAsync(manager, EnodeB, updateFile: true);

        Assert.That(await File.ReadAllTextAsync(tempPath.Path), Is.EqualTo(original), "a no-op remove must not clobber a file that was never loaded (e.g. after a failed init)");
    }

    private async Task<TManager> CreateManagerWithLoadedFile(TempPath tempPath)
    {
        await File.WriteAllTextAsync(tempPath.Path, "[]");
        TManager manager = CreateManager(tempPath.Path);
        await InitAsync(manager);
        return manager;
    }

    private static string PublicKeyOf(string enode) => enode.Substring("enode://".Length, 128);
}

[TestFixture]
public class StaticNodesManagerPersistenceTests : NodesManagerPersistenceTests<StaticNodesManager>
{
    protected override StaticNodesManager CreateManager(string path) => new(path, LimboLogs.Instance);
    protected override Task InitAsync(StaticNodesManager manager) => manager.InitAsync();
    protected override Task<bool> AddAsync(StaticNodesManager manager, string enode, bool updateFile) => manager.AddAsync(new NetworkNode(enode), updateFile);
    protected override Task<bool> RemoveAsync(StaticNodesManager manager, string enode, bool updateFile) => manager.RemoveAsync(new NetworkNode(enode), updateFile);
}

[TestFixture]
public class TrustedNodesManagerPersistenceTests : NodesManagerPersistenceTests<TrustedNodesManager>
{
    protected override TrustedNodesManager CreateManager(string path) => new(path, LimboLogs.Instance);
    protected override Task InitAsync(TrustedNodesManager manager) => manager.InitAsync();
    protected override Task<bool> AddAsync(TrustedNodesManager manager, string enode, bool updateFile) => manager.AddAsync(new Enode(enode), updateFile);
    protected override Task<bool> RemoveAsync(TrustedNodesManager manager, string enode, bool updateFile) => manager.RemoveAsync(new Enode(enode), updateFile);
}
