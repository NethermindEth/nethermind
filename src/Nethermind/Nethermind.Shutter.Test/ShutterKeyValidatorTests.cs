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
        ShutterEventSimulator eventSimulator = ShutterTestsCommon.InitEventSimulator(rnd);

        bool eventFired = false;
        api.KeyValidator.KeysValidated += (_, _) => eventFired = true;

        api.AdvanceSlot(5);

        Assert.That(eventFired);
    }

    [Test]
    public void Rejects_outdated_decryption_keys()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd);
        ShutterEventSimulator eventSimulator = ShutterTestsCommon.InitEventSimulator(rnd);

        (List<ShutterEventSimulator.Event> events, Dto.DecryptionKeys keys) = api.AdvanceSlot(5);

        bool eventFired = false;
        api.KeyValidator.KeysValidated += (_, _) => eventFired = true;

        // same keys are now outdated
        api.TriggerKeysReceived(keys);

        Assert.That(eventFired, Is.False);
    }
}
