// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Shutter;

public interface IShutterKeyValidator
{
    void OnDecryptionKeysReceived(Dto.DecryptionKeys decryptionKeys);
    event EventHandler<ValidatedKeyArgs> KeysValidated;

    public struct ValidatedKeyArgs
    {
        public ulong Slot;
        public ulong Eon;
        public ulong TxPointer;
        public List<(byte[] IdentityPreimage, byte[] Key)> Keys;
    }
}
