// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.AccountAbstraction.Bundler
{
    public interface IBundler
    {
        public void Bundle(Block head);
    }
}
