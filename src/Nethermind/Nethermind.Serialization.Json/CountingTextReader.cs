//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Serialization.Json
{
    public class CountingTextReader : TextReader
    {
        private readonly TextReader _innerReader;
        public int Length { get; private set; }

        public CountingTextReader(TextReader innerReader)
        {
            _innerReader = innerReader;
        }

        public override void Close()
        {
            base.Close();
            _innerReader.Close();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _innerReader.Dispose();
            }
        }

        public override int Peek() => _innerReader.Peek();

        public override int Read()
        {
            Length++;
            return _innerReader.Read();
        }

        public override int Read(char[] buffer, int index, int count) => IncrementLength(_innerReader.Read(buffer, index, count));

        public override int Read(Span<char> buffer) => IncrementLength(_innerReader.Read(buffer));

        public override async Task<int> ReadAsync(char[] buffer, int index, int count) => IncrementLength(await _innerReader.ReadAsync(buffer, index, count));

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) => IncrementLength(await _innerReader.ReadAsync(buffer, cancellationToken));

        public override int ReadBlock(char[] buffer, int index, int count) => IncrementLength(_innerReader.ReadBlock(buffer, index, count));

        public override int ReadBlock(Span<char> buffer) => IncrementLength(_innerReader.ReadBlock(buffer));

        public override string ReadLine() => IncrementLength(_innerReader.ReadLine());

        public override async ValueTask<int> ReadBlockAsync(Memory<char> buffer, CancellationToken cancellationToken = default) => IncrementLength(await _innerReader.ReadBlockAsync(buffer, cancellationToken));

        public override async Task<int> ReadBlockAsync(char[] buffer, int index, int count) => IncrementLength(await _innerReader.ReadBlockAsync(buffer, index, count));

        public override async Task<string> ReadLineAsync() => IncrementLength(await _innerReader.ReadLineAsync());

        public override string ReadToEnd() => IncrementLength(_innerReader.ReadToEnd());

        public override async Task<string> ReadToEndAsync() => IncrementLength(await _innerReader.ReadToEndAsync());

        private string IncrementLength(in string read)
        {
            if (!string.IsNullOrEmpty(read))
            {
                Length += read.Length;
            }
            return read;
        }

        private int IncrementLength(in int read)
        {
            Length += read;
            return read;
        }
    }
}
