// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.FastSync;

public interface ITreeSync
{
    public event EventHandler<VerifyPostSyncCleanupEventArgs> OnVerifyPostSyncCleanup;

    public class VerifyPostSyncCleanupEventArgs(Hash256 root) : EventArgs
    {
        public Hash256 Root => root;
    }
}