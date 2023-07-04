// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain
{
    public enum AddBlockResult
    {
        AlreadyKnown,
        CannotAccept,
        UnknownParent,
        InvalidBlock,
        Added
    }
}
