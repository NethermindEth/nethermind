// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using NSubstitute;
using Nethermind.Logging;
using Nethermind.Shutter.Config;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Abi;
using Nethermind.State;
using Nethermind.Specs;
using Nethermind.Core;

namespace Nethermind.Shutter.Test;

[TestFixture]
class ShutterMessageHandlerTests
{
    [Test]
    public void Can_accept_valid_decryption_keys()
    {
        ShutterMessageHandler msgHandler = CreateMessageHandler();
        bool eventFired = false;
        msgHandler.KeysValidated += (_, _) => eventFired = true;
        msgHandler.OnDecryptionKeysReceived(new Dto.DecryptionKeys()
        {

        });
        Assert.That(eventFired);
    }

    [Test]
    public void Can_reject_invalid_decryption_keys()
    {
        ShutterMessageHandler msgHandler = CreateMessageHandler();
        bool eventFired = false;
        msgHandler.KeysValidated += (_, _) => eventFired = true;
        msgHandler.OnDecryptionKeysReceived(new Dto.DecryptionKeys()
        {

        });
        Assert.That(!eventFired);
    }

    [Test]
    public void Can_reject_outdated_decryption_keys()
    {
        ShutterMessageHandler msgHandler = CreateMessageHandler();
        bool eventFired = false;
        msgHandler.KeysValidated += (_, _) => eventFired = true;
        msgHandler.OnDecryptionKeysReceived(new Dto.DecryptionKeys()
        {

        });
        Assert.That(!eventFired);
    }

    private ShutterMessageHandler CreateMessageHandler()
    {
        ShutterConfig cfg = new()
        {
            KeyBroadcastContractAddress = Address.Zero.ToString(),
            KeyperSetManagerContractAddress = Address.Zero.ToString(),
        };

        // ShutterTxSource txSource = Substitute.For<ShutterTxSource>();

        // txSource
        //     .When(x => x.LoadTransactions(Arg.Any<ulong>(), Arg.Any<ulong>()))
        //     .Do(x => { return; });
        IReadOnlyBlockTree readOnlyBlockTree = Substitute.For<IReadOnlyBlockTree>();

        ReadOnlyTxProcessingEnvFactory txProcessingEnvFactory = new(
            Substitute.For<IWorldStateManager>(),
            readOnlyBlockTree,
            GnosisSpecProvider.Instance,
            LimboLogs.Instance
        );

        ShutterEon eon = new(
            readOnlyBlockTree,
            txProcessingEnvFactory,
            Substitute.For<IAbiEncoder>(),
            cfg,
            LimboLogs.Instance
        );
        // eon.GetCurrentEonInfo().Returns(x => null);

        return new ShutterMessageHandler(cfg, eon, LimboLogs.Instance);
    }
}
