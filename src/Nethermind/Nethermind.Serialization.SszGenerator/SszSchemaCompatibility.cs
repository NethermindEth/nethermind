// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

internal static class SszSchemaCompatibility
{
    public static bool AreCompatible(SszSchema left, SszSchema right) => (left, right) switch
    {
        (BasicSchema first, BasicSchema second) => first.Identity == second.Identity,
        (SequenceSchema first, SequenceSchema second) => first.Kind == second.Kind && first.Bound == second.Bound && AreCompatible(first.Element, second.Element),
        (BitSequenceSchema first, BitSequenceSchema second) => first.Kind == second.Kind && first.Bound == second.Bound,
        (ContainerSchema first, ContainerSchema second) => first.Kind == second.Kind && ContainersAreCompatible(first, second),
        (UnionSchema first, UnionSchema second) => UnionsAreCompatible(first, second),
        (CodecParameterSchema first, CodecParameterSchema second) => first.Identity == second.Identity,
        _ => false,
    };

    private static bool ContainersAreCompatible(ContainerSchema first, ContainerSchema second)
    {
        if (first.Kind == ContainerKind.Container)
        {
            if (first.Fields.Length != second.Fields.Length) return false;
            for (int index = 0; index < first.Fields.Length; index++)
            {
                if (first.Fields[index].Name != second.Fields[index].Name || !AreCompatible(first.Fields[index].Schema, second.Fields[index].Schema)) return false;
            }
            return true;
        }
        foreach (SszField left in first.Fields)
        {
            foreach (SszField right in second.Fields)
            {
                if ((left.Index == right.Index || left.Name == right.Name)
                    && (left.Index != right.Index || left.Name != right.Name || !AreCompatible(left.Schema, right.Schema))) return false;
            }
        }
        return true;
    }

    private static bool UnionsAreCompatible(UnionSchema first, UnionSchema second)
    {
        // Compatibility is not transitive; every cross-union option pair must agree.
        // https://ethereum.github.io/consensus-specs/ssz/simple-serialize/#compatible-merkleization
        foreach (SszSchema left in first.Options)
        {
            foreach (SszSchema right in second.Options)
            {
                if (!AreCompatible(left, right)) return false;
            }
        }
        return true;
    }
}
