// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Shutter;

public interface IShutterKeyValidator
{
    ValidatedKeys? ValidateKeys(Dto.DecryptionKeys decryptionKeys);

    public struct ValidatedKeys
    {
        public ulong Slot;
        public ulong Eon;
        public ulong TxPointer;
        public List<(byte[] IdentityPreimage, byte[] Key)> Keys;
    }
}
