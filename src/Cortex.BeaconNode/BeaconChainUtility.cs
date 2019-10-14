using System;
using System.Collections.Generic;
using Cortex.Containers;

namespace Cortex.BeaconNode
{
    public class BeaconChainUtility
    {
        private readonly ICryptographyService _cryptographyService;

        public BeaconChainUtility(ICryptographyService cryptographyService)
        {
            _cryptographyService = cryptographyService;
        }

        /// <summary>
        /// Returns the domain for the 'domain_type' and 'fork_version'
        /// </summary>
        public Domain ComputeDomain(DomainType domainType, ForkVersion forkVersion = new ForkVersion())
        {
            var combined = new Span<byte>(new byte[Domain.Length]);
            domainType.AsSpan().CopyTo(combined);
            forkVersion.AsSpan().CopyTo(combined.Slice(DomainType.Length));
            return new Domain(combined);
        }

        /// <summary>
        /// Check if 'leaf' at 'index' verifies against the Merkle 'root' and 'branch'
        /// </summary>
        public bool IsValidMerkleBranch(Hash32 leaf, IReadOnlyList<Hash32> branch, int depth, ulong index, Hash32 root)
        {
            var value = leaf;
            for (var testDepth = 0; testDepth < depth; testDepth++)
            {
                var branchValue = branch[testDepth];
                var indexAtDepth = index / ((ulong)1 << testDepth);
                if (indexAtDepth % 2 == 0)
                {
                    // Branch on right
                    value = _cryptographyService.Hash(value, branchValue);
                }
                else
                {
                    // Branch on left
                    value = _cryptographyService.Hash(branchValue, value);
                }
            }
            return value.Equals(root);
        }
    }
}
