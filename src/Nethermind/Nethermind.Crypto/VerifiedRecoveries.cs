// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

/// <summary>Looks up public keys whose recovery inputs have already been verified.</summary>
internal sealed class VerifiedRecoveries
{
    private readonly byte[] _messages;
    private readonly byte[] _signatures;
    private readonly byte[] _recoveryIds;
    private readonly byte[] _keys;
    private readonly Dictionary<RecoveryKey, int>? _indices;

    internal VerifiedRecoveries(byte[] messages, byte[] signatures, byte[] recoveryIds, byte[] keys)
    {
        _messages = messages;
        _signatures = signatures;
        _recoveryIds = recoveryIds;
        _keys = keys;
        if (recoveryIds.Length <= 64) return;

        _indices = new Dictionary<RecoveryKey, int>(recoveryIds.Length);
        for (int i = 0; i < recoveryIds.Length; i++)
            _indices.TryAdd(Key(signatures.AsSpan(i * 64, 64), recoveryIds[i], messages.AsSpan(i * 32, 32)), i);
    }

    internal bool TryGetKey(ReadOnlySpan<byte> signature, byte recoveryId, ReadOnlySpan<byte> message, Span<byte> publicKey)
    {
        if (message.Length != 32 || signature.Length != 64) return false;

        if (_indices is not null)
        {
            if (!_indices.TryGetValue(Key(signature, recoveryId, message), out int index)) return false;
            _keys.AsSpan(index * 64, 64).CopyTo(publicKey);
            return true;
        }

        for (int i = 0; i < _recoveryIds.Length; i++)
        {
            if (_recoveryIds[i] == recoveryId
                && message.SequenceEqual(_messages.AsSpan(i * 32, 32))
                && signature.SequenceEqual(_signatures.AsSpan(i * 64, 64)))
            {
                _keys.AsSpan(i * 64, 64).CopyTo(publicKey);
                return true;
            }
        }
        return false;
    }

    private static RecoveryKey Key(ReadOnlySpan<byte> signature, byte recoveryId, ReadOnlySpan<byte> message) =>
        new(new ValueHash256(message), new ValueHash256(signature[..32]), new ValueHash256(signature[32..]), recoveryId);

    private readonly record struct RecoveryKey(ValueHash256 Message, ValueHash256 R, ValueHash256 S, byte RecoveryId);
}
