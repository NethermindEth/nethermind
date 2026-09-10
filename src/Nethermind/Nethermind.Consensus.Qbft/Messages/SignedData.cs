// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Qbft.Messages;

/// <summary>A payload together with its signature and the author recovered from it.</summary>
public sealed class SignedData<T>(T payload, Signature signature, Address author) : IEquatable<SignedData<T>> where T : QbftPayload
{
    public T Payload { get; } = payload;
    public Signature Signature { get; } = signature;
    public Address Author { get; } = author;

    public bool Equals(SignedData<T>? other) =>
        other is not null && Author == other.Author && Signature.Equals(other.Signature) && Payload.Equals(other.Payload);

    public override bool Equals(object? obj) => Equals(obj as SignedData<T>);

    public override int GetHashCode() => HashCode.Combine(Author, Signature, Payload);

    public override string ToString() => $"SignedData[author={Author}, payload={Payload}]";
}
