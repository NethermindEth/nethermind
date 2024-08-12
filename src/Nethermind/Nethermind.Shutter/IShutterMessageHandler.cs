// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Shutter;
public interface IShutterMessageHandler
{
    void OnDecryptionKeysReceived(Dto.DecryptionKeys decryptionKeys);
    event EventHandler<Dto.DecryptionKeys> KeysValidated;
}
