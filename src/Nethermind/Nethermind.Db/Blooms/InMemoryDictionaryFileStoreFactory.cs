// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db.Blooms
{
    public class InMemoryDictionaryFileStoreFactory : IFileStoreFactory
    {
        public IFileStore Create(string name) => new InMemoryDictionaryFileStore();
    }
}
