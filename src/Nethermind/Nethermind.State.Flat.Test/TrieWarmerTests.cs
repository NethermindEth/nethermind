// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.ScopeProvider;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class TrieWarmerTests
{
    private IProcessExitSource _processExitSource = null!;
    private CancellationTokenSource _cts = null!;
    private ILogManager _logManager = null!;
    private FlatDbConfig _config = null!;

    [SetUp]
    public void SetUp()
    {
        _cts = new CancellationTokenSource();
        _processExitSource = Substitute.For<IProcessExitSource>();
        _processExitSource.Token.Returns(_cts.Token);
        _logManager = LimboLogs.Instance;
        _config = new FlatDbConfig { TrieWarmerWorkerCount = 2 };
    }

    [TearDown]
    public void TearDown() => _cts?.Dispose();

    [Test]
    public async Task PushAddressJob_CallsWarmUpStateTrie()
    {
        TrieWarmer warmer = new TrieWarmer(_processExitSource, _logManager, _config);

        ITrieWarmer.IAddressWarmer addressWarmer = Substitute.For<ITrieWarmer.IAddressWarmer>();
        Address address = new Address("0x1234567890123456789012345678901234567890");

        warmer.PushAddressJob(addressWarmer, address, sequenceId: 1, isWrite: false);

        await Task.Delay(200);

        addressWarmer.Received().WarmUpStateTrie(address, 1, false);

        _cts.Cancel();
        await warmer.DisposeAsync();
    }

    [Test]
    public async Task PushSlotJob_CallsWarmUpStorageTrie()
    {
        TrieWarmer warmer = new TrieWarmer(_processExitSource, _logManager, _config);

        ITrieWarmer.IStorageWarmer storageWarmer = Substitute.For<ITrieWarmer.IStorageWarmer>();
        UInt256 index = 42;

        warmer.PushSlotJob(storageWarmer, index, sequenceId: 5, isWrite: false);

        await Task.Delay(200);

        storageWarmer.Received().WarmUpStorageTrie(index, 5, false);

        _cts.Cancel();
        await warmer.DisposeAsync();
    }

    [Test]
    public async Task PushAddressJob_PassesCorrectSequenceId()
    {
        TrieWarmer warmer = new TrieWarmer(_processExitSource, _logManager, _config);

        ITrieWarmer.IAddressWarmer addressWarmer = Substitute.For<ITrieWarmer.IAddressWarmer>();
        Address address = new Address("0x1111111111111111111111111111111111111111");

        warmer.PushAddressJob(addressWarmer, address, sequenceId: 999, isWrite: false);

        await Task.Delay(200);

        addressWarmer.Received().WarmUpStateTrie(address, 999, false);

        _cts.Cancel();
        await warmer.DisposeAsync();
    }

    [Test]
    public async Task PushJobMulti_WithAddressOnly_CallsWarmUpStateTrie()
    {
        TrieWarmer warmer = new TrieWarmer(_processExitSource, _logManager, _config);

        ITrieWarmer.IAddressWarmer addressWarmer = Substitute.For<ITrieWarmer.IAddressWarmer>();
        Address address = new Address("0x2222222222222222222222222222222222222222");

        warmer.PushJobMulti(addressWarmer, address, storageTree: null, index: null, sequenceId: 10, isWrite: false);

        await Task.Delay(200);

        addressWarmer.Received().WarmUpStateTrie(address, 10, false);

        _cts.Cancel();
        await warmer.DisposeAsync();
    }

    [Test]
    public async Task PushJobMulti_WithStorageWarmer_CallsWarmUpStorageTrie()
    {
        TrieWarmer warmer = new TrieWarmer(_processExitSource, _logManager, _config);

        ITrieWarmer.IAddressWarmer addressWarmer = Substitute.For<ITrieWarmer.IAddressWarmer>();
        ITrieWarmer.IStorageWarmer storageWarmer = Substitute.For<ITrieWarmer.IStorageWarmer>();
        Address address = new Address("0x3333333333333333333333333333333333333333");
        UInt256 index = 100;

        warmer.PushJobMulti(addressWarmer, address, storageWarmer, index, sequenceId: 20, isWrite: true);

        await Task.Delay(200);

        storageWarmer.Received().WarmUpStorageTrie(index, 20, true);
        addressWarmer.DidNotReceive().WarmUpStateTrie(Arg.Any<Address>(), Arg.Any<int>(), Arg.Any<bool>());

        _cts.Cancel();
        await warmer.DisposeAsync();
    }
}
