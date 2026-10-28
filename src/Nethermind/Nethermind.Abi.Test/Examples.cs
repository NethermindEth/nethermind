// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class Examples
{
    [Test]
    public void CommitHashFromHash256()
    {
        AbiSignature<(ValueHash256, byte[])> signature = new AbiSignature("commitHash")
            .With(
                AbiType.Bytes32,
                AbiType.Bytes);

        Hash256 secretHash = Keccak.Compute("secret");
        byte[] cipher = "cipher"u8.ToArray();
        byte[] encoded = AbiCodec.Encode(signature, (secretHash, cipher));
        (ValueHash256 decodedHash, _) = AbiCodec.Decode(signature, encoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Is.EqualTo(AbiCodec.Encode(signature, (secretHash.ValueHash256, cipher))));
            Assert.That(decodedHash.ToHash256(), Is.EqualTo(secretHash));
        }
    }
}
