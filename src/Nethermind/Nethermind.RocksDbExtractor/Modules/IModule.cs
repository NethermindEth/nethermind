// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using Terminal.Gui;

namespace Nethermind.RocksDbExtractor.Modules
{
    public interface IModule
    {
        Window Init();
    }
}
