// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable annotations

using System;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class ValidationResultTests
{
    [Test]
    public void Classified_failure_preserves_the_existing_message_contract_and_result_size()
    {
        const string message = "nonce exceeds max nonce";
        ValidationResult plain = new(message);
        ValidationResult classified = new(message, TxValidationError.NonceCapExceeded);
        classified.Deconstruct(out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Unsafe.SizeOf<ValidationResult>(), Is.EqualTo(IntPtr.Size));
            Assert.That(classified.AsBool(), Is.False);
            Assert.That(classified.ToString(), Is.EqualTo(message));
            Assert.That(error, Is.SameAs(message));
            Assert.That(classified, Is.EqualTo(plain));
            Assert.That(classified.GetHashCode(), Is.EqualTo(plain.GetHashCode()));
            Assert.That(classified.TxError, Is.EqualTo(TxValidationError.NonceCapExceeded));
            Assert.That(plain.TxError, Is.EqualTo(TxValidationError.None));
        }
    }
}
