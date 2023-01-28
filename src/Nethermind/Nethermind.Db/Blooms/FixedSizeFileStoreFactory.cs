// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Logging;

namespace Nethermind.Db.Blooms
{
    public class FixedSizeFileStoreFactory : IFileStoreFactory
    {
        private readonly string _basePath;
        private readonly string _extension;
        private readonly int _elementSize;

        public FixedSizeFileStoreFactory(string basePath, string extension, int elementSize)
        {
            _basePath = string.Empty.GetApplicationResourcePath(basePath);
            _extension = extension;
            _elementSize = elementSize;
            Directory.CreateDirectory(_basePath);
        }

        public IFileStore Create(string name) => new FixedSizeFileStore(Path.Combine(_basePath, name + "." + _extension), _elementSize);
    }
}
