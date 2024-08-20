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
        ShutterKeyValidator keyValidator = ShutterTestsCommon.InitKeyValidator();
        bool eventFired = false;
        keyValidator.KeysValidated += (_, _) => eventFired = true;
        keyValidator.OnDecryptionKeysReceived(new Dto.DecryptionKeys()
        {

        });
        Assert.That(eventFired);
    }

    [Test]
    public void Can_reject_invalid_decryption_keys()
    {
        ShutterKeyValidator keyValidator = ShutterTestsCommon.InitKeyValidator();
        bool eventFired = false;
        keyValidator.KeysValidated += (_, _) => eventFired = true;
        keyValidator.OnDecryptionKeysReceived(new Dto.DecryptionKeys()
        {

        });
        Assert.That(!eventFired);
    }

    [Test]
    public void Can_reject_outdated_decryption_keys()
    {
        ShutterKeyValidator keyValidator = ShutterTestsCommon.InitKeyValidator();
        bool eventFired = false;
        keyValidator.KeysValidated += (_, _) => eventFired = true;
        keyValidator.OnDecryptionKeysReceived(new Dto.DecryptionKeys()
        {

        });
        Assert.That(!eventFired);
    }
}
