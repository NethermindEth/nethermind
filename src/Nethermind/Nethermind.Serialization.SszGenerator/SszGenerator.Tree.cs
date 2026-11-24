// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

public partial class SszGenerator
{
    /// <summary>The expression for the number of leaves that a tree over the items of <paramref name="property"/> has room for.</summary>
    private static string LeafLimit(SszProperty property)
    {
        string count = $"{(property.Kind == Kind.Vector ? (ulong)property.Length!.Value : property.Limit!.Value)}UL";
        return property.IsPacked ? $"(({count} * {property.Type.StaticLength}UL + 31UL) / 32UL)" : count;
    }

    /// <summary>A delegate that merkleizes one encoded item of <paramref name="type"/>.</summary>
    private static string ItemHasher(SszType type) => type.Kind == Kind.Basic || type.IsTypeParameter
        ? $"static (global::System.ReadOnlySpan<byte> item, out UInt256 itemRoot) => {{ {MerkleizeEncodedValue(type, "item", "itemRoot")} }}"
        : $"{type.StaticMemberAccess}.MerkleizeEncoded";

    private static string MerkleizeEncodedValue(SszType type, string bytes, string root) => type switch
    {
        // C# cannot call a static method that is not in the constraint through a type parameter.
        { IsTypeParameter: true } => $"{type.TypeReferenceName}.Decode({bytes}, out {type.TypeReferenceName} decoded); {type.TypeReferenceName}.Merkleize(decoded, out {root});",
        { Kind: Kind.Basic } => $"Merkle.Merkleize(out {root}, {bytes});",
        _ => $"{type.StaticMemberAccess}.MerkleizeEncoded({bytes}, out {root});",
    };

    /// <summary>The statements that merkleize the encoded value of <paramref name="property"/>.</summary>
    private static string MerkleizeEncodedStatement(SszProperty property, string bytes, string root)
    {
        const string count = "count";
        switch (property.Kind)
        {
            case Kind.BitVector: return $"Merkle.Merkleize(out {root}, {bytes});";
            case Kind.BitList: return $"MerkleizeEncodedBitList({bytes}, {(property.Limit + 255) / 256}UL, out {root});";
            case Kind.ProgressiveBitList: return $"MerkleizeEncodedBitList({bytes}, 0UL, out {root});";
            case Kind.Vector when property.IsPacked: return $"Merkle.Merkleize(out {root}, {bytes}, {LeafLimit(property)});";
            case Kind.List when property.IsPacked: return $"Merkle.Merkleize(out {root}, {bytes}, {LeafLimit(property)}); Merkle.MixIn(ref {root}, {bytes}.Length / {property.Type.StaticLength});";
            case Kind.ProgressiveList when property.IsPacked: return $"Merkle.MerkleizeProgressive(out {root}, {bytes}); Merkle.MixIn(ref {root}, {bytes}.Length / {property.Type.StaticLength});";
            case Kind.Vector or Kind.List or Kind.ProgressiveList:
                string limit = property.Kind == Kind.ProgressiveList ? "0UL" : LeafLimit(property);
                string merkleize = $"MerkleizeEncodedItems({bytes}, {(property.Type.IsVariable ? 0 : property.Type.StaticLength)}, {limit}, {ItemHasher(property.Type)}, out {root}, out int {count});";
                return property.Kind == Kind.Vector ? merkleize : $"{merkleize} Merkle.MixIn(ref {root}, {count});";
            default: return MerkleizeEncodedValue(property.Type, bytes, root);
        }
    }

    /// <summary>The statements that read the collections of an encoded value, which checks their limits and counts.</summary>
    /// <remarks>Merkleization does not cover items above the limit, so without this they could be changed without changing the root.</remarks>
    private static IEnumerable<string> CollectionChecks(IEnumerable<SszProperty> members, string reader) => members
        .Where(m => m.IsCollection || (m.Kind & (Kind.BitVector | Kind.BitList | Kind.ProgressiveBitList)) != Kind.None)
        .Select(m => $"_ = {reader}.{m.Name};");

    private static string MerkleizeEncodedCode(SszType decl)
    {
        List<string> body = [];
        if (decl.IsSszListItself)
        {
            body.AddRange(CollectionChecks(decl.Members!, "new RefReader(data)"));
            body.Add(MerkleizeEncodedStatement(decl.Members![0], "data", "root"));
        }
        else if (decl.Kind == Kind.CompatibleUnion)
        {
            body.Add("RefReader reader = new(data);");
            body.Add("switch (reader.Selector)");
            body.Add("{");
            body.AddRange(decl.CompatibleUnionMembers!.Select(m =>
                $"    case {decl.Selector!.Type.StaticMemberAccess}.{m.Name}: {{ {string.Concat(CollectionChecks([m], "reader"))} {MerkleizeEncodedStatement(m, "data.Slice(1)", "root")} break; }}"));
            body.Add($"    default: root = ThrowInvalidSszData<UInt256>(nameof({decl.TypeReferenceName}), $\"unsupported union selector {{data[0]}}.\"); break;");
            body.Add("}");
            body.Add("Merkle.MixIn(ref root, data[0]);");
        }
        else
        {
            SszProperty[] members = decl.Members!;
            body.Add("RefReader reader = new(data);");
            body.AddRange(CollectionChecks(members, "reader"));
            body.Add($"Span<UInt256> leaves = stackalloc UInt256[{members.Length}];");
            body.AddRange(members.Select((m, i) => $"{{ {MerkleizeEncodedStatement(m, MemberBytes(members, m, "data"), $"leaves[{i}]")} }}"));
            body.Add(decl.Kind == Kind.ProgressiveContainer ? "Merkle.MerkleizeProgressive(out root, leaves);" : "Merkle.Merkleize(out root, leaves);");
            if (decl.Kind == Kind.ProgressiveContainer) body.Add($"Merkle.MixInActiveFields(ref root, {ByteArrayLiteral(decl.ActiveFieldsBytes ?? [])});");
        }

        return $@"    /// <summary>Merkleizes an encoded value without decoding it.</summary>
    /// <remarks>Checks the layout of the value and the limits of its lists.</remarks>
    /// <exception cref=""System.IO.InvalidDataException"">The layout is invalid, or a list exceeds its limit.</exception>
    public {(decl.HasSszBaseType ? "new " : "")}static void MerkleizeEncoded({ReaderBytesTypeName} data, out UInt256 root)
    {{
{Shift(2, body)}
    }}";
    }

    /// <summary>The expression for the layout of the tree of a member that has one.</summary>
    private static string TreeShape(SszProperty property)
    {
        if (!property.IsCollection) return $"{property.Type.StaticMemberAccess}.RefTreeReader.TreeShape";

        bool isProgressive = property.Kind == Kind.ProgressiveList;
        string depth = isProgressive ? "0" : $"Merkle.NextPowerOfTwoExponent({LeafLimit(property)})";
        return $"new SszTreeShape({depth}, {(isProgressive ? "true" : "false")}, {(property.Type.HasTree ? "SszTreeShape.PerLeaf" : "0")})";
    }

    /// <summary>The expression for how the items of a collection that has a tree become its leaves.</summary>
    private static string TreeItems(SszProperty property)
    {
        SszType item = property.Type;
        string hash = property.IsPacked || item.HasTree ? "null" : ItemHasher(item);
        string build = item.HasTree
            ? $"static (output, item) => {{ using {item.StaticMemberAccess}.TreeWriter tree = new(output); tree.Write(new {item.StaticMemberAccess}.RefReader(item)); }}"
            : "null";
        string itemShape = item.HasTree ? $"{item.StaticMemberAccess}.RefTreeReader.TreeShape" : "default";
        return $"new SszTreeItems({TreeShape(property)}, {(item.IsVariable ? 0 : item.StaticLength)}, {(property.Kind == Kind.Vector ? "false" : "true")}, {hash}, {build}, {itemShape})";
    }

    /// <summary>The members in the order that they are written in: fixed-size ones first.</summary>
    private static SszProperty[] WriteOrder(SszProperty[] members) =>
        [.. members.Where(m => !m.IsVariable), .. members.Where(m => m.IsVariable)];

    private static string TreeReaderTypeName(SszProperty property) => property switch
    {
        { IsCollection: false } => $"{property.Type.StaticMemberAccess}.RefTreeReader",
        { Type.HasTree: true } => $"SszListTreeReader<{property.Type.StaticMemberAccess}.RefTreeReader>",
        _ => "SszTreeReader",
    };

    private static string ReadTreeExpression(SszProperty property, string tree) => property switch
    {
        { IsCollection: false } => $"new({tree})",
        { Type.HasTree: true } => $"new({tree}, {property.Type.StaticMemberAccess}.RefTreeReader.TreeShape, static item => new(item))",
        _ => tree,
    };

    private static string TreeCode(SszType decl)
    {
        if (!decl.HasTree) return string.Empty;

        SszProperty[] members = decl.Members!;
        SszProperty[] writeOrder = WriteOrder(members);
        SszProperty[] treeMembers = [.. writeOrder.Where(m => m.HasTree)];
        bool isCollection = decl.IsSszListItself;
        bool isProgressive = decl.Kind == Kind.ProgressiveContainer;
        string shape = isCollection ? TreeShape(members[0])
            : $"new SszTreeShape(Merkle.NextPowerOfTwoExponent({members.Length}UL), {(isProgressive ? "true" : "false")}, {treeMembers.Length})";
        string activeFields = isProgressive ? ByteArrayLiteral(decl.ActiveFieldsBytes ?? []) : "default";

        List<string> readers = [];
        List<string> writers = [];
        List<string> writeAll = [];
        for (int i = 0; i < writeOrder.Length; i++)
        {
            SszProperty member = writeOrder[i];
            int leaf = Array.IndexOf(members, member);
            int inner = Array.IndexOf(treeMembers, member);
            string claim = isCollection ? string.Empty : $"_output!.BeginMember(_id, {i}, {leaf}, nameof({member.Name})); ";
            string checkSource = $"if (!HasSource) ThrowNoSszSource(nameof({member.Name})); ";
            string sourceRoot = $"_source.{member.Name}Root";
            string sourceTree = isCollection ? "_source._tree" : $"_source.TreeOf{member.Name}";
            writeAll.Add($"Write{member.Name}({(isCollection ? "value._data" : MemberBytes(members, member, "value._data"))});");

            if (!isCollection) readers.Add($"public UInt256 {member.Name}Root {{ get => _tree[{leaf}]; }}");
            if (!member.HasTree)
            {
                writers.Add($"public void Write{member.Name}({ReaderBytesTypeName} encoded) {{ {claim}UInt256 root; {MerkleizeEncodedStatement(member, "encoded", "root")} _output!.EndMember(in root); }}");
                writers.Add($"public void Copy{member.Name}() {{ {checkSource}{claim}_output!.EndMember({sourceRoot}); }}");
                continue;
            }

            if (isCollection)
            {
                readers.Add($"public {TreeReaderTypeName(member)} {member.Name} {{ get => {ReadTreeExpression(member, "_tree")}; }}");
            }
            else
            {
                string previousShape = inner == 0 ? "default" : TreeShape(treeMembers[inner - 1]);
                readers.Add($"internal SszTreeReader TreeOf{member.Name} {{ get => _tree.Exists ? _tree.Inner({inner}, {previousShape}, {TreeShape(member)}) : default; }}");
                readers.Add($"public {TreeReaderTypeName(member)} {member.Name} {{ get => {ReadTreeExpression(member, $"TreeOf{member.Name}")}; }}");
                writers.Add($"public void Copy{member.Name}() {{ {checkSource}{claim}_output!.CopyTree({sourceRoot}, {sourceTree}); }}");
            }

            if (!member.IsCollection)
            {
                string type = member.Type.StaticMemberAccess;
                writers.Add($"public {type}.TreeWriter Begin{member.Name}() {{ {claim}return new(_output!, new {type}.RefTreeReader({sourceTree})); }}");
                writers.Add($"public void Write{member.Name}({ReaderBytesTypeName} encoded) {{ using {type}.TreeWriter tree = Begin{member.Name}(); tree.Write(new {type}.RefReader(encoded)); }}");
                continue;
            }

            string list = member.Type.HasTree ? "tree.List" : "tree";
            string listType = member.Type.HasTree ? $"SszVariableListTreeWriter<{member.Type.StaticMemberAccess}.TreeWriter>" : "SszListTreeWriter";
            string beginItem = member.Type.HasTree ? $", static (output, source) => new(output, new {member.Type.StaticMemberAccess}.RefTreeReader(source))" : string.Empty;
            writers.Add($"public {listType} Begin{member.Name}() {{ {claim}return new(_output!, {TreeItems(member)}, {sourceTree}{beginItem}); }}");
            writers.Add($"public void Write{member.Name}({ReaderBytesTypeName} encoded) {{ using {listType} tree = Begin{member.Name}(); {list}.AddAll(encoded); }}");
            if (isCollection) writers.Add($"public void Copy{member.Name}({ReaderBytesTypeName} encoded) {{ using {listType} tree = Begin{member.Name}(); {list}.CopyAll(encoded); }}");
        }

        string hides = decl.HasTreeBaseType ? "new " : "";
        return $@"    /// <summary>Writes the hash tree of an encoded value.</summary>
    public static void WriteTree(SszTreeWriter output, RefReader value)
    {{
        using TreeWriter tree = new(output);
        tree.Write(value);
    }}
{Whitespace}
    /// <summary>Reads the hash tree of a value.</summary>
    public {hides}readonly ref struct RefTreeReader
    {{
        internal readonly SszTreeReader _tree;
{Whitespace}
        public static SszTreeShape TreeShape {{ get => {shape}; }}
{Whitespace}
        public RefTreeReader(SszTreeReader tree)
        {{
            _tree = tree;
        }}
{Whitespace}
{(isCollection ? "" : $@"        /// <param name=""trees"">The trees of a value, of which the last is the tree of the value itself.</param>
        public RefTreeReader({ReaderBytesTypeName} trees)
        {{
            _tree = SszTreeReader.Last(trees, {members.Length}, TreeShape);
        }}")}
{Whitespace}
{Shift(2, readers)}
    }}
{Whitespace}
    /// <summary>Writes the hash tree of a value from its encoded members, in the order of its <c>Writer</c>.</summary>
    public {hides}readonly ref struct TreeWriter
    {{
        internal readonly SszTreeWriter? _output;
        private readonly RefTreeReader _source;
        {(isCollection ? "" : "private readonly int _id;")}
{Whitespace}
        public TreeWriter(SszTreeWriter output) : this(output, default(RefTreeReader))
        {{
        }}
{Whitespace}
        /// <param name=""output"">Receives the tree.</param>
        /// <param name=""source"">The tree to copy unchanged parts from.</param>
        public TreeWriter(SszTreeWriter output, RefTreeReader source)
        {{
            _output = output;
            _source = source;
            {(isCollection ? "" : $"_id = output.OpenContainer({members.Length});")}
        }}
{Whitespace}
        internal bool IsOpen {{ get => _output is not null; }}
        internal bool HasSource {{ get => _source._tree.Exists; }}
{Whitespace}
        /// <summary>Writes the tree of a whole value.</summary>
        /// <exception cref=""System.IO.InvalidDataException"">The layout is invalid, or a list exceeds its limit.</exception>
        public void Write(RefReader value)
        {{
{Shift(3, CollectionChecks(members, "value").Concat(writeAll))}
        }}
{Whitespace}
{Shift(2, writers)}
{Whitespace}
        {(isCollection ? "" : @"/// <summary>Begins a member whose writer reports its hash, if this tree is written.</summary>
        internal SszTreeWriter? BeginHashed(int index, int leaf, string name)
        {
            _output?.BeginMember(_id, index, leaf, name);
            return _output;
        }")}
{Whitespace}
        /// <summary>Writes the tree if all members are written.</summary>
        public void Dispose()
        {{
            {(isCollection ? "" : $"_output?.CloseContainer(_id, {members.Length}, RefTreeReader.TreeShape, _source._tree, {activeFields});")}
        }}
    }}";
    }

    /// <summary>The statements that write and copy <paramref name="member"/> in the tree of its container, and what its nested writer reports to.</summary>
    private static (string Write, string Copy, string Nested) WriterTree(SszType decl, SszProperty member, int writeIndex, string sourceBytes)
    {
        bool isListWriter = member.IsCollection;
        if (!decl.HasTree) return ("", "", isListWriter || member.Type.HasTree ? "default" : "null");

        string write = $"if (_tree.IsOpen) _tree.Write{member.Name}(data);";
        string copy = decl.IsSszListItself
            ? $"if (_tree.IsOpen) _tree.Copy{member.Name}({sourceBytes});"
            : $"if (_tree.HasSource) _tree.Copy{member.Name}(); else if (_tree.IsOpen) _tree.Write{member.Name}({sourceBytes});";
        if (member.HasTree)
        {
            string list = isListWriter && member.Type.HasTree ? ".List" : "";
            return (write, copy, $"_tree.IsOpen ? _tree.Begin{member.Name}(){list} : default");
        }

        string beginHashed = $"_tree.BeginHashed({writeIndex}, {Array.IndexOf(decl.Members!, member)}, nameof({member.Name}))";
        string hasher = $"static (global::System.ReadOnlySpan<byte> encoded, out UInt256 root) => {{ {MerkleizeEncodedStatement(member, "encoded", "root")} }}";
        return (write, copy, isListWriter ? $"_tree.IsOpen ? new SszListTreeWriter({beginHashed}!, {hasher}) : default" : beginHashed);
    }
}
