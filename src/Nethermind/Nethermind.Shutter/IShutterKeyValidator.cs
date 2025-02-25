// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;

namespace Nethermind.Shutter;

public interface IShutterKeyValidator
{
    ValidatedKeys? ValidateKeys(Dto.DecryptionKeys decryptionKeys);

    struct ValidatedKeys
    {
        public ulong Slot;
        public ulong Eon;
        public ulong TxPointer;
        public EnumerableWithCount<(ReadOnlyMemory<byte> IdentityPreimage, ReadOnlyMemory<byte> Key)> Keys;
    }
}
