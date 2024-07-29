// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery;

// In a class so that it can atomically change.
record AckInfo(ushort seq_nr, byte[]? selectiveAckData);
