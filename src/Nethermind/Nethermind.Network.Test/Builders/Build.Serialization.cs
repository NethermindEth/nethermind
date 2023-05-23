// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;

namespace Nethermind.Network.Test.Builders
{
    public static class BuildExtensions
    {
        public static SerializationBuilder SerializationService(this Build build)
        {
            return new();
        }
    }
}
