// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap;

public interface ISnapEntry
{
    ValueHash256 Path { get; }
    byte[] ToRlpValue();
}
