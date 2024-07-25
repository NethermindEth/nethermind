using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Core.Crypto;
using System.Linq;

namespace Nethermind.Db
{

    public class LogIndexStorage
    {
        private Dictionary<AddressAsKey, int[]> _addressToBlocks = new Dictionary<AddressAsKey, int[]>();
        private Dictionary<Hash256, int[]> _topicsToBlocks = new Dictionary<Hash256, int[]>();

        //TODO: Add Methods AddReceipts
        //TODO: Add Methods for Filter

        public void LoadFile(Address address, string path)
        {
            const int maxLineLength = 1024;

            if (!File.Exists(path))
            {
                return;
            }
            using SafeFileHandle fileHandle = File.OpenHandle(path, FileMode.OpenOrCreate);

            byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(maxLineLength);
            long offset = 0;

            int read = RandomAccess.Read(fileHandle, rentedBuffer, offset);
            Span<byte> bytes = default;
            while (read > 0)
            {
                offset += read;
                bytes = rentedBuffer.AsSpan(0, read);
                int[] intData = new int[read / 4];

                TurboPFor.p4nddec128v32(bytes.ToArray(), read / 4, intData);

                _addressToBlocks[address] = intData;
                read = RandomAccess.Read(fileHandle, rentedBuffer, offset);
            }

            ArrayPool<byte>.Shared.Return(rentedBuffer);

        }

        public void StoreLogIndex(Address address, long blockNumber)
        {

            if (!_addressToBlocks.TryGetValue(address, out int[] blocks))
            {
                blocks = new int[0];
                _addressToBlocks[address] = blocks;
            }
            blocks = blocks.Append((int)blockNumber).ToArray();
            _addressToBlocks[address] = blocks;
        }

        public IEnumerable<int> GetBlocksForAddress(Address address)
        {
            if (_addressToBlocks.TryGetValue(address, out int[] blocks))
            {
                return blocks;
            }
            return Array.Empty<int>();
        }


        public IEnumerable<int> GetBlocksForTopic(Hash256 topic)
        {
            return [1, 2, 3, 4, 5, 6];
        }

    }

}
