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
    public void TearDown()
    {
        _cts?.Dispose();
    }

    [Test]
    public async Task PushAddressJob_CallsWarmUpStateTrie()
    {
        var warmer = new TrieWarmer(_processExitSource, _logManager, _config);

        var addressWarmer = Substitute.For<ITrieWarmer.IAddressWarmer>();
        var address = new Address("0x1234567890123456789012345678901234567890");

        warmer.PushAddressJob(addressWarmer, address, sequenceId: 1, isWrite: false);

        await Task.Delay(200);

        addressWarmer.Received().WarmUpStateTrie(address, 1, false);

        _cts.Cancel();
        await warmer.DisposeAsync();
    }

    [Test]
    public async Task PushSlotJob_CallsWarmUpStorageTrie()
    {
        var warmer = new TrieWarmer(_processExitSource, _logManager, _config);

        var storageWarmer = Substitute.For<ITrieWarmer.IStorageWarmer>();
        UInt256 index = 42;

        warmer.PushSlotJob(storageWarmer, index, sequenceId: 5, isWrite: true);

        await Task.Delay(200);

        storageWarmer.Received().WarmUpStorageTrie(index, 5, true);

        _cts.Cancel();
        await warmer.DisposeAsync();
    }

    [Test]
    public async Task PushJobMulti_WithStorageTree_CallsWarmUpStorageTrie()
    {
        var warmer = new TrieWarmer(_processExitSource, _logManager, _config);

        var addressWarmer = Substitute.For<ITrieWarmer.IAddressWarmer>();
        var storageWarmer = Substitute.For<ITrieWarmer.IStorageWarmer>();
        var address = new Address("0x1234567890123456789012345678901234567890");
        UInt256 index = 100;

        warmer.PushJobMulti(addressWarmer, address, storageWarmer, index, sequenceId: 3, isWrite: false);

        await Task.Delay(200);

        storageWarmer.Received().WarmUpStorageTrie(index, 3, false);
        addressWarmer.DidNotReceive().WarmUpStateTrie(Arg.Any<Address>(), Arg.Any<int>(), Arg.Any<bool>());

        _cts.Cancel();
        await warmer.DisposeAsync();
    }

    [Test]
    public async Task PushJobMulti_WithoutStorageTree_CallsWarmUpStateTrie()
    {
        var warmer = new TrieWarmer(_processExitSource, _logManager, _config);

        var addressWarmer = Substitute.For<ITrieWarmer.IAddressWarmer>();
        var address = new Address("0xabcdef0123456789abcdef0123456789abcdef01");
        UInt256 index = 200;

        warmer.PushJobMulti(addressWarmer, address, storageTree: null, index, sequenceId: 7, isWrite: true);

        await Task.Delay(200);

        addressWarmer.Received().WarmUpStateTrie(address, 7, true);

        _cts.Cancel();
        await warmer.DisposeAsync();
    }

    [Test]
    public async Task PushAddressJob_PassesCorrectSequenceId()
    {
        var warmer = new TrieWarmer(_processExitSource, _logManager, _config);

        var addressWarmer = Substitute.For<ITrieWarmer.IAddressWarmer>();
        var address = new Address("0x1111111111111111111111111111111111111111");

        warmer.PushAddressJob(addressWarmer, address, sequenceId: 999, isWrite: false);

        await Task.Delay(200);

        addressWarmer.Received().WarmUpStateTrie(address, 999, Arg.Any<bool>());

        _cts.Cancel();
        await warmer.DisposeAsync();
    }

    [Test]
    public async Task PushAddressJob_PassesCorrectIsWriteFlag()
    {
        var warmer = new TrieWarmer(_processExitSource, _logManager, _config);

        var addressWarmerRead = Substitute.For<ITrieWarmer.IAddressWarmer>();
        var addressWarmerWrite = Substitute.For<ITrieWarmer.IAddressWarmer>();
        var address1 = new Address("0x2222222222222222222222222222222222222222");
        var address2 = new Address("0x3333333333333333333333333333333333333333");

        warmer.PushAddressJob(addressWarmerRead, address1, sequenceId: 1, isWrite: false);
        warmer.PushAddressJob(addressWarmerWrite, address2, sequenceId: 2, isWrite: true);

        await Task.Delay(200);

        addressWarmerRead.Received().WarmUpStateTrie(address1, Arg.Any<int>(), false);
        addressWarmerWrite.Received().WarmUpStateTrie(address2, Arg.Any<int>(), true);

        _cts.Cancel();
        await warmer.DisposeAsync();
    }
}
