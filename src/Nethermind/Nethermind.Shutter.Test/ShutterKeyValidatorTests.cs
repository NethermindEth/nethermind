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

    [Test]
    public void Rejects_decryption_keys_with_out_of_range_signer_index()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd);

        (List<ShutterEventSimulator.Event> _, Dto.DecryptionKeys keys) = api.AdvanceSlot(5);
        Assert.That(api.KeysValidated, Is.EqualTo(1));

        // Re-target a fresh slot so the message is not skipped, then point the first signer index
        // past the keyper address list. Previously this hit an unchecked array access in
        // CheckDecryptionKeys and threw IndexOutOfRangeException; it must now be rejected gracefully.
        keys.Gnosis.Slot += 1;
        keys.Gnosis.SignerIndices[0] = ulong.MaxValue;

        IShutterKeyValidator.ValidatedKeys? result = null;
        Assert.DoesNotThrow(() => result = api.KeyValidator.ValidateKeys(keys));
        Assert.That(result, Is.Null);
        Assert.That(api.KeysValidated, Is.EqualTo(1));
    }

    [Test]
    public void Rejects_decryption_keys_with_wrong_length_signature()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd);

        (List<ShutterEventSimulator.Event> _, Dto.DecryptionKeys keys) = api.AdvanceSlot(5);
        Assert.That(api.KeysValidated, Is.EqualTo(1));

        // Re-target a fresh slot, then truncate the first signature. CheckSlotDecryptionIdentitiesSignature
        // indexes signatureBytes[64]; a short signature previously threw IndexOutOfRangeException.
        keys.Gnosis.Slot += 1;
        keys.Gnosis.Signatures[0] = ByteString.CopyFrom(new byte[10]);

        IShutterKeyValidator.ValidatedKeys? result = null;
        Assert.DoesNotThrow(() => result = api.KeyValidator.ValidateKeys(keys));
        Assert.That(result, Is.Null);
        Assert.That(api.KeysValidated, Is.EqualTo(1));
    }
}
