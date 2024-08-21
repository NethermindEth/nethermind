// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Shutter.Test;

[TestFixture]
class ShutterKeyValidatorTests
{
    [Test]
    public void Can_accept_valid_decryption_keys()
    {
        ShutterApiTests api = ShutterTestsCommon.InitApi();

        IShutterKeyValidator keyValidator = api.KeyValidator;
        bool eventFired = false;
        keyValidator.KeysValidated += (_, _) => eventFired = true;

        api.SetEon(new() {

        });
        api.TriggerKeysReceived(new Dto.DecryptionKeys());

        Assert.That(eventFired);
    }

    [Test]
    public void Can_reject_invalid_decryption_keys()
    {
        ShutterApiTests api = ShutterTestsCommon.InitApi();

        IShutterKeyValidator keyValidator = api.KeyValidator;
        bool eventFired = false;
        keyValidator.KeysValidated += (_, _) => eventFired = true;

        api.SetEon(new() {

        });
        api.TriggerKeysReceived(new Dto.DecryptionKeys());

        Assert.That(eventFired, Is.False);
    }

    [Test]
    public void Can_reject_outdated_decryption_keys()
    {
        ShutterApiTests api = ShutterTestsCommon.InitApi();
        IShutterKeyValidator keyValidator = api.KeyValidator;

        api.SetEon(new() {

        });

        Dto.DecryptionKeys keys = new();

        // load up to date keys
        api.TriggerKeysReceived(keys);

        bool eventFired = false;
        keyValidator.KeysValidated += (_, _) => eventFired = true;

        // same slot, should be outdated
        api.TriggerKeysReceived(keys);

        Assert.That(eventFired, Is.False);
    }
}
