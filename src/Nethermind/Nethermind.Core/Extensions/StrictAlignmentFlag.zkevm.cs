// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Extensions;

/// <summary>Whether the guest's JIT guards every access it cannot prove aligned with a run-time alignment test.</summary>
/// <remarks>
/// SP1 rejects misaligned loads and stores, so bflat builds its guest with strict alignment. The JIT then proves
/// alignment only from how an address is formed, and a byref parameter or a pointer read from memory proves nothing.
/// <see cref="IsActive"/> is <see langword="false"/> as written; the SP1 guest's <c>substitutions.xml</c> stubs it
/// to <see langword="true"/> at link time, and ILC folds every check it gates.
/// </remarks>
internal readonly struct StrictAlignmentFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
