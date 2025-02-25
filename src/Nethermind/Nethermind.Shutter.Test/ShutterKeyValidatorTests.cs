// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Nethermind.Shutter.Test;

[TestFixture]
class ShutterKeyValidatorTests
{
    [Test]
    public void Accepts_valid_decryption_keys()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd);

        Assert.That(api.KeysValidated, Is.EqualTo(0));

        api.AdvanceSlot(5);

        Assert.That(api.KeysValidated, Is.EqualTo(1));
    }

    [Test]
    public void Rejects_outdated_decryption_keys()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd);

        (List<ShutterEventSimulator.Event> _, Dto.DecryptionKeys keys) = api.AdvanceSlot(5);

        Assert.That(api.KeysValidated, Is.EqualTo(1));

        // should ignore more keys from the same slot
        api.TriggerKeysReceived(keys);

        Assert.That(api.KeysValidated, Is.EqualTo(1));
    }
}
