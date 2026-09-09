// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

// A stack slot holds a word in its UInt256 limb layout, so the host build no longer needs its own
// big-endian readers and writers; the byte-oriented boundaries reverse through EvmWord.ByteSwap.
public ref partial struct EvmStack;
