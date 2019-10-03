using System;
using System.Collections.Generic;
using System.Linq;

namespace Cortex.SimpleSerialize
{
    public class SszContainer : SszNode
    {
        private const int BITS_PER_BYTE = 8;
        private const int BYTES_PER_LENGTH_OFFSET = 4;
        private const int MAX_LENGTH = 2 ^ (BYTES_PER_LENGTH_OFFSET * BITS_PER_BYTE);

        private readonly IEnumerable<SszNode> _orderedNodes;

        public SszContainer(IEnumerable<SszNode> orderedNodes)
        {
            _orderedNodes = orderedNodes;
        }

        public override bool IsVariableSize
        {
            get
            {
                return _orderedNodes.Any(x => x.IsVariableSize);
            }
        }

        public override ReadOnlySpan<byte> HashTreeRoot()
        {
            var hashes = _orderedNodes.SelectMany(x => x.HashTreeRoot().ToArray());
            return Merkleize(hashes.ToArray());
        }

        public override ReadOnlySpan<byte> Serialize()
        {
            // Basic implementation of algorithm directly from specifications to understand how it works
            // No optimisations yet; in fact probably want to change to use a vistor-like pattern overall.

            var fixedParts = _orderedNodes.Select(x => !x.IsVariableSize ? x.Serialize().ToArray() : null);
            var variableParts = _orderedNodes.Select(x => x.IsVariableSize ? x.Serialize().ToArray() : new byte[0]);

            var fixedLengths = fixedParts.Select(x => x != null ? x.Length : BYTES_PER_LENGTH_OFFSET);
            var variableLengths = variableParts.Select(x => x.Length);

            var totalLength = fixedLengths.Sum() + variableLengths.Sum();
            if (totalLength >= MAX_LENGTH)
            {
                throw new InvalidOperationException("Container data length exceeded");
            }

            var variableOffsets = variableParts
                .Select((x, i) => new SszNumber((uint)(fixedLengths.Sum() + variableLengths.Take(i).Sum())).Serialize().ToArray())
                .ToList();
            var interleaved = fixedParts.Select((x, i) => x ?? variableOffsets[i]);

            var result = interleaved.SelectMany(x => x).Concat(variableParts.SelectMany(x => x));
            return result.ToArray();
        }
    }
}
