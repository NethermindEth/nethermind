// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.AuRa.Shutter;
public interface IShutterMessageHandler
{
    void OnDecryptionKeysReceived(Dto.DecryptionKeys decryptionKeys);
}
