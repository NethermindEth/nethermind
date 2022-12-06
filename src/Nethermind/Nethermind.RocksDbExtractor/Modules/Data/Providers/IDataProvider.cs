// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

namespace Nethermind.RocksDbExtractor.Modules.Data.Providers
{
    public interface IDataProvider
    {
        void Init(string path);
    }
}
