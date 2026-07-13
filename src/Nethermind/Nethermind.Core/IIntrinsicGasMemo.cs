// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Opaque per-transaction memo slot for a cached intrinsic-gas result. The concrete type is owned
/// by the gas policy in the EVM layer, which Core cannot reference; this marker types the
/// <see cref="Transaction.IntrinsicGasMemo"/> field so the slot is constrained and named rather
/// than a bare <see cref="object"/>.
/// </summary>
public interface IIntrinsicGasMemo;
