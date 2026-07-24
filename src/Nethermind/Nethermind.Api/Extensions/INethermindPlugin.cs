// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using Nethermind.Core.Attributes;

namespace Nethermind.Api.Extensions;

[StableApi]
public interface INethermindPlugin
{
    string Name { get; }

    string Description { get; }

    string Author { get; }

    void InitTxTypesAndRlpDecoders(INethermindApi api) { }

    bool MustInitialize => false;
    bool Enabled { get; }
    IModule? Module => null;
}
