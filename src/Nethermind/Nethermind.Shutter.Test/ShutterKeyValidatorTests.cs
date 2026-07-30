// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Google.Protobuf;
using NUnit.Framework;

namespace Nethermind.Shutter.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
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

    private static IEnumerable<TestCaseData> MalformedDecryptionKeysCases()
    {
        yield return new TestCaseData((Action<Dto.DecryptionKeys>)(keys => keys.Gnosis.SignerIndices[0] = ulong.MaxValue))
            .SetName("signer index out of range");
        yield return new TestCaseData((Action<Dto.DecryptionKeys>)(keys => keys.Gnosis.Signatures[0] = ByteString.CopyFrom(new byte[10])))
            .SetName("wrong-length signature");
    }

    [TestCaseSource(nameof(MalformedDecryptionKeysCases))]
    public void Rejects_malformed_decryption_keys(Action<Dto.DecryptionKeys> corrupt)
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd);

        (List<ShutterEventSimulator.Event> _, Dto.DecryptionKeys keys) = api.AdvanceSlot(5);
        Assert.That(api.KeysValidated, Is.EqualTo(1));

        keys.Gnosis.Slot += 1;
        corrupt(keys);

        IShutterKeyValidator.ValidatedKeys? result = null;
        Assert.DoesNotThrow(() => result = api.KeyValidator.ValidateKeys(keys));
        Assert.That(result, Is.Null);
        Assert.That(api.KeysValidated, Is.EqualTo(1));
    }
}
