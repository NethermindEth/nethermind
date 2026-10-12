// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;

Rlp.RegisterDecoder(typeof(Account), AccountDecoder.Instance);
Rlp.RegisterDecoder(typeof(BlockHeader), new HeaderDecoder());
Rlp.RegisterDecoder(typeof(Transaction), TxDecoder.Instance);
