//// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
//// SPDX-License-Identifier: LGPL-3.0-only

//using Nethermind.Core.Buffers;
//using Nethermind.Core.Specs;
//using Nethermind.Core;
//using Nethermind.Serialization.Rlp;

//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;
//using Nethermind.Core.Crypto;
//using ILGPU;
//using ILGPU.Runtime;
//using ILGPU.Util;

//namespace Nethermind.State.Proofs;
//public class GpuTrie<TType>
//{
//    private readonly IRlpStreamDecoder<TType> _decoder;
//    /// <inheritdoc/>
//    /// <param name="receipts">The transaction receipts to build the trie of.</param>
//    public GpuTrie(IReleaseSpec spec, TType[] receipts, IRlpStreamDecoder<TType> trieDecoder)
//    {
//        ArgumentNullException.ThrowIfNull(spec);
//        ArgumentNullException.ThrowIfNull(receipts);
//        ArgumentNullException.ThrowIfNull(trieDecoder);
//        _decoder = trieDecoder;

//        //if (receipts.Length > 0)
//        //{
//        //    Initialize(receipts, spec);
//        //    UpdateRootHash();
//        //}
//    }

//        /// <summary>
//    /// Entry method to compute the root hash of the Ethereum PMT using GPU acceleration.
//    /// </summary>
//    public Hash256 GetRootHash(TType[] values, RlpBehaviors behavior)
//    {
//        if (KeccakHash.SupportsGpu){; }

//        // Prepare keys and values
//        int key = 0;

//        List<byte[]> keysList = new(values.Length);
//        List<byte[]> valuesList = new(values.Length);
//        foreach (TType receipt in values)
//        {
//            byte[] value = _decoder.EncodeToCappedArray(receipt, behavior, _bufferPool).ToArray();
//            valuesList.Add(value);

//            byte[] keyBytes = BitConverter.GetBytes(key++);
//            keysList.Add(keyBytes);
//        }

//        // Flatten keys and values
//        (byte[] flatKeys, int[] keyOffsets) = FlattenByteArrays(keysList);
//        (byte[] flatValues, int[] valueOffsets) = FlattenByteArrays(valuesList);

        
//        {
//            // Prepare data for GPU
//            var accelerator = GPUManager.GetAccelerator();

//            // Allocate buffers
//            var keysBuffer = accelerator.Allocate1D(flatKeys);
//            var keyOffsetsBuffer = accelerator.Allocate1D(keyOffsets);

//            var valuesBuffer = accelerator.Allocate1D(flatValues);
//            var valueOffsetsBuffer = accelerator.Allocate1D(valueOffsets);

//            // Estimate node count (over-allocate to be safe)
//            int estimatedNodeCount = flatKeys.Length * 2; // Adjust as needed
//            var nodesBuffer = accelerator.Allocate1D<GpuMptNode>(estimatedNodeCount);

//            // Initialize nodes to default values
//            accelerator.MemorySet(nodesBuffer.View, default(GpuMptNode));

//            // Initialize NodeCount
//            var nodeCountBuffer = accelerator.Allocate1D<int>(1);
//            nodeCountBuffer.MemSetToZero();

//            // Initialize context
//            var context = new GpuMptContext
//            {
//                Nodes = nodesBuffer.View,
//                NodeCount = nodeCountBuffer.View,
//                Keys = keysBuffer.View,
//                KeyOffsets = keyOffsetsBuffer.View,
//                KeyCount = keyOffsets.Length,
//                Values = valuesBuffer.View,
//                ValueOffsets = valueOffsetsBuffer.View,
//                ValueCount = valueOffsets.Length,
//                Levels = 0,
//                CurrentLevel = 0
//            };

//            // Initialize root node
//            InitializeRootNode(accelerator, context);

//            // Launch Trie Insertion Kernel
//            var constructKernel = accelerator.LoadAutoGroupedStreamKernel<
//                Index1D,
//                GpuMptContext>(GpuMptConstructKernel);

//            constructKernel((int)values.Length, context);

//            accelerator.Synchronize();

//            // Compute levels and set LevelStarts and LevelEnds
//            ComputeLevels(accelerator, context);

//            // Launch Hash Computation Kernels level by level
//            for (int level = context.Levels - 1; level >= 0; level--)
//            {
//                context.CurrentLevel = level;

//                int nodesAtLevel = context.LevelEnds[level] - context.LevelStarts[level];
//                if (nodesAtLevel > 0)
//                {
//                    var hashKernel = accelerator.LoadAutoGroupedStreamKernel<
//                        Index1D,
//                        GpuMptContext>(GpuMptHashComputeKernel);

//                    hashKernel(nodesAtLevel, context);

//                    accelerator.Synchronize();
//                }
//            }

//            // Retrieve the root hash
//            GpuMptNode[] rootNodeArray = nodesBuffer.GetAsArray1D(accelerator)[..1]; // Get the first node
//            GpuMptNode rootNode = rootNodeArray[0];

//            // Extract the hash
//            byte[] rootHashBytes = new byte[32];
//            unsafe
//            {
//                fixed (ulong* hashPtr = rootNode.Hash)
//                {
//                    Buffer.MemoryCopy(hashPtr, rootHashBytes.AsMemory().Pin().Pointer, 32, 32);
//                }
//            }

//            Hash256 rootHash = new Hash256(rootHashBytes);

//            // Dispose GPU resources
//            keysBuffer.Dispose();
//            keyOffsetsBuffer.Dispose();
//            valuesBuffer.Dispose();
//            valueOffsetsBuffer.Dispose();
//            nodesBuffer.Dispose();
//            nodeCountBuffer.Dispose();

//            return rootHash;
//        }
//    }

//    /// <summary>
//    /// Flattens a list of byte arrays into a single byte array and computes cumulative offsets.
//    /// </summary>
//    private (byte[] FlatArray, int[] Offsets) FlattenByteArrays(List<byte[]> arrays)
//    {
//        int totalLength = arrays.Sum(a => a.Length);
//        byte[] flatArray = new byte[totalLength];
//        int[] offsets = new int[arrays.Count];

//        int offset = 0;
//        for (int i = 0; i < arrays.Count; i++)
//        {
//            byte[] array = arrays[i];
//            Array.Copy(array, 0, flatArray, offset, array.Length);
//            offset += array.Length;
//            offsets[i] = offset;
//        }

//        return (flatArray, offsets);
//    }

//    /// <summary>
//    /// Initializes the root node in the trie.
//    /// </summary>
//    private void InitializeRootNode(Accelerator accelerator, GpuMptContext context)
//    {
//        var rootNode = new GpuMptNode
//        {
//            NodeType = 0, // Branch node
//            KeyFragmentOffset = -1,
//            KeyFragmentLength = 0,
//            ValueIndex = -1,
//            IsProcessed = 0
//        };

//        unsafe
//        {
//            for (int i = 0; i < 17; i++)
//            {
//                rootNode.Children[i] = -1;
//            }

//            for (int i = 0; i < 4; i++)
//            {
//                rootNode.Hash[i] = 0;
//            }
//        }

//        context.Nodes.SubView(0, 1).CopyFromCPU(new GpuMptNode[] { rootNode });

//        // Set initial node count to 1
//        context.NodeCount[0] = 1;
//    }

//    /// <summary>
//    /// Computes the levels in the trie and sets LevelStarts and LevelEnds in the context.
//    /// </summary>
//    private void ComputeLevels(Accelerator accelerator, GpuMptContext context)
//    {
//        // Retrieve nodes from GPU
//        int nodeCount = context.NodeCount[0];
//        ArrayView<GpuMptNode> nodes = context.Nodes.SubView(0, nodeCount);

//        // Perform BFS traversal to assign levels
//        Dictionary<int, int> nodeLevels = new();
//        Queue<int> queue = new();
//        queue.Enqueue(0);
//        nodeLevels[0] = 0;
//        int maxLevel = 0;

//        while (queue.Count > 0)
//        {
//            int currentNodeIndex = queue.Dequeue();
//            int currentLevel = nodeLevels[currentNodeIndex];
//            GpuMptNode node = nodes[currentNodeIndex];

//            unsafe
//            {
//                for (int i = 0; i < 17; i++)
//                {
//                    int childIndex = node.Children[i];
//                    if (childIndex != -1 && !nodeLevels.ContainsKey(childIndex))
//                    {
//                        nodeLevels[childIndex] = currentLevel + 1;
//                        maxLevel = Math.Max(maxLevel, currentLevel + 1);
//                        queue.Enqueue(childIndex);
//                    }
//                }
//            }
//        }

//        context.Levels = maxLevel + 1;

//        // Prepare LevelStarts and LevelEnds arrays
//        int[] levelStarts = new int[context.Levels];
//        int[] levelEnds = new int[context.Levels];
//        List<int>[] levels = new List<int>[context.Levels];
//        for (int i = 0; i < context.Levels; i++)
//        {
//            levels[i] = new List<int>();
//        }

//        foreach (var kvp in nodeLevels)
//        {
//            int nodeIndex = kvp.Key;
//            int level = kvp.Value;
//            levels[level].Add(nodeIndex);
//        }

//        int currentStart = 0;
//        for (int i = 0; i < context.Levels; i++)
//        {
//            levelStarts[i] = currentStart;
//            int levelNodeCount = levels[i].Count;
//            currentStart += levelNodeCount;
//            levelEnds[i] = currentStart;
//        }

//        // Flatten levels into a single array
//        int[] levelNodeIndices = levels.SelectMany(l => l).ToArray();

//        // Update context
//        context.LevelStarts = accelerator.Allocate1D(levelStarts).View;
//        context.LevelEnds = accelerator.Allocate1D(levelEnds).View;

//        // Reorder nodes in context.Nodes according to levels
//        GpuMptNode[] reorderedNodes = new GpuMptNode[nodeCount];
//        for (int i = 0; i < nodeCount; i++)
//        {
//            int originalIndex = levelNodeIndices[i];
//            reorderedNodes[i] = nodes[originalIndex];
//        }

//        // Copy reordered nodes back to GPU
//        context.Nodes.SubView(0, nodeCount).CopyFromCPU(reorderedNodes);
//    }

//    /// <summary>
//    /// Kernel function to construct the trie by inserting key-value pairs.
//    /// </summary>
//public unsafe static void GpuMptConstructKernel(
//    Index1D index,
//    GpuMptContext context)
//{
//    int idx = index.X;

//    // Compute key start and end offsets
//    int keyStartOffset = idx == 0 ? 0 : context.KeyOffsets[idx - 1];
//    int keyEndOffset = context.KeyOffsets[idx];
//    int keyLength = keyEndOffset - keyStartOffset;

//    // Compute value start and end offsets
//    int valueStartOffset = idx == 0 ? 0 : context.ValueOffsets[idx - 1];
//    int valueEndOffset = context.ValueOffsets[idx];
//    int valueLength = valueEndOffset - valueStartOffset;

//    // Begin traversal from root node
//    int currentNodeIndex = 0; // Root node index
//    int keyPos = 0;           // Position in the key (in nibbles)
//    int totalNibbles = keyLength * 2; // Each byte contains two nibbles

//    while (keyPos < totalNibbles)
//    {
//        byte nibble = GetNibble(context.Keys, keyStartOffset, keyPos);

//        // Get the current node
//        GpuMptNode currentNode = context.Nodes[currentNodeIndex];

//        // Check if the current node is uninitialized (first insertion)
//        if (currentNode.NodeType == -1)
//        {
//            // Initialize the node as an extension node
//            currentNode.NodeType = 1; // Extension node
//            currentNode.KeyFragmentOffset = keyStartOffset * 2 + keyPos;
//            currentNode.KeyFragmentLength = 0; // Will be incremented later
//            currentNode.ValueIndex = -1;
//            currentNode.IsProcessed = 0;

//            // Initialize children to -1
//            for (int i = 0; i < 17; i++)
//            {
//                currentNode.Children[i] = -1;
//            }
//        }

//        // Increment the key fragment length
//        currentNode.KeyFragmentLength += 1; // Add one nibble

//        // Check if a child node exists for the current nibble
//        int childNodeIndex = currentNode.Children[nibble];

//        if (childNodeIndex == -1)
//        {
//            // No child exists; create a new node
//            int newNodeIndex = Atomic.Add(ref context.NodeCount[0], 1);

//            GpuMptNode newNode = new GpuMptNode
//            {
//                NodeType = -1, // Uninitialized
//                KeyFragmentOffset = -1,
//                KeyFragmentLength = 0,
//                ValueIndex = -1,
//                IsProcessed = 0
//            };

//            // Initialize children to -1
//            for (int i = 0; i < 17; i++)
//            {
//                newNode.Children[i] = -1;
//            }

//            context.Nodes[newNodeIndex] = newNode;

//            // Set the child node index
//            currentNode.Children[nibble] = newNodeIndex;
//            context.Nodes[currentNodeIndex] = currentNode; // Update the node

//            currentNodeIndex = newNodeIndex;
//            keyPos++;
//        }
//        else
//        {
//            // Child node exists; move to the child
//            context.Nodes[currentNodeIndex] = currentNode; // Update the node
//            currentNodeIndex = childNodeIndex;
//            keyPos++;
//        }
//    }

//    // At the end of the key, set the value at the leaf node
//    GpuMptNode leafNode = context.Nodes[currentNodeIndex];
//    leafNode.NodeType = 2; // Leaf node
//    leafNode.ValueIndex = idx; // Index into ValueOffsets array
//    context.Nodes[currentNodeIndex] = leafNode;
//}

//    /// <summary>
//    /// Retrieves a nibble (4 bits) from the keys array.
//    /// </summary>
//public static byte GetNibble(ArrayView<byte> keys, int keyStartOffset, int nibbleIndex)
//{
//    int byteIndex = keyStartOffset + (nibbleIndex / 2);
//    byte keyByte = keys[byteIndex];

//    if (nibbleIndex % 2 == 0)
//    {
//        return (byte)(keyByte >> 4);
//    }
//    else
//    {
//        return (byte)(keyByte & 0x0F);
//    }
//}

//    /// <summary>
//    /// Kernel function to compute hashes of trie nodes level by level.
//    /// </summary>
//public static void GpuMptHashComputeKernel(
//    Index1D index,
//    GpuMptContext context)
//{
//    int nodeIndex = context.LevelStarts[context.CurrentLevel] + index.X;
//    GpuMptNode node = context.Nodes[nodeIndex];

//    if (node.IsProcessed == 1)
//        return;

//    // Each thread uses its own portion of the buffers
//    int rlpOffset = nodeIndex * context.MaxRlpLengthPerNode;
//    var rlpBuffer = context.RlpEncodeBuffer.SubView(rlpOffset, context.MaxRlpLengthPerNode);

//    int rlpItemOffset = nodeIndex * context.MaxRlpItemsPerNode;
//    var rlpItems = context.RlpItemsList.SubView(rlpItemOffset, context.MaxRlpItemsPerNode);

//    int rlpLength = 0;

//    // Create RLP item for the node
//    RlpItem rlpNode = CreateRlpItemForNode(node, context, rlpItems);

//    // Encode the RLP item
//    RlpEncode(ref rlpNode, rlpBuffer, ref rlpLength);

//    // Compute the hash
//    ulong[] hash = Keccak256(rlpBuffer.SubView(0, rlpLength));

//    // Store the hash in the node
//    unsafe
//    {
//        for (int i = 0; i < 4; i++)
//        {
//            node.Hash[i] = hash[i];
//        }
//    }

//    node.IsProcessed = 1;
//    context.Nodes[nodeIndex] = node;
//}


//    /// <summary>
//    /// RLP encodes a leaf node.
//    /// </summary>
//public static byte[] RlpEncodeLeafNode(GpuMptNode node, GpuMptContext context)
//{
//    // Get the key fragment
//    int fragmentStartNibble = node.KeyFragmentOffset;
//    int fragmentLength = node.KeyFragmentLength;

//    byte[] keyFragment = new byte[(fragmentLength + 1) / 2];
//    for (int i = 0; i < fragmentLength; i++)
//    {
//        byte nibble = GetNibble(context.Keys, 0, fragmentStartNibble + i);
//        if (i % 2 == 0)
//        {
//            keyFragment[i / 2] = (byte)(nibble << 4);
//        }
//        else
//        {
//            keyFragment[i / 2] |= nibble;
//        }
//    }

//    // Apply hex prefix encoding with terminator flag
//    byte[] encodedPath = HexPrefixEncode(keyFragment, true);

//    // Get the value
//    int valueIndex = node.ValueIndex;
//    int valueStartOffset = valueIndex == 0 ? 0 : context.ValueOffsets[valueIndex - 1];
//    int valueEndOffset = context.ValueOffsets[valueIndex];
//    int valueLength = valueEndOffset - valueStartOffset;

//    byte[] value = new byte[valueLength];
//    for (int i = 0; i < valueLength; i++)
//    {
//        value[i] = context.Values[valueStartOffset + i];
//    }

//    // RLP encode the node: [encodedPath, value]
//    RlpList rlpNode = new RlpList
//    {
//        new RlpString(encodedPath),
//        new RlpString(value)
//    };

//    return Rlp.Encode(rlpNode);
//}
//public struct RlpItem
//{
//    public bool IsList; // Indicates if this item is a list
//    public int Length;  // Length of the data or number of items in the list
//    public ArrayView<byte> Data;        // For strings: the data bytes
//    public ArrayView<RlpItem> ListItems; // For lists: the items in the list
//}
//    /// <summary>
//    /// RLP encodes an extension node.
//    /// </summary>
//public static byte[] RlpEncodeExtensionNode(GpuMptNode node, GpuMptContext context, ulong[][] childHashes)
//{
//    // Get the key fragment
//    int fragmentStartNibble = node.KeyFragmentOffset;
//    int fragmentLength = node.KeyFragmentLength;

//    byte[] keyFragment = new byte[(fragmentLength + 1) / 2];
//    for (int i = 0; i < fragmentLength; i++)
//    {
//        byte nibble = GetNibble(context.Keys, 0, fragmentStartNibble + i);
//        if (i % 2 == 0)
//        {
//            keyFragment[i / 2] = (byte)(nibble << 4);
//        }
//        else
//        {
//            keyFragment[i / 2] |= nibble;
//        }
//    }

//    // Apply hex prefix encoding without terminator flag
//    byte[] encodedPath = HexPrefixEncode(keyFragment, false);

//    // The child node is the branch node we just processed
//    // Assume childHash is the hash of the branch node
//    byte[] childHashBytes = new byte[32];
//    for (int i = 0; i < 32; i++)
//    {
//        childHashBytes[i] = (byte)(childHashes[0][i / 8] >> (8 * (7 - (i % 8))));
//    }

//    // RLP encode the node: [encodedPath, child]
//    RlpList rlpNode = new RlpList
//    {
//        new RlpString(encodedPath),
//        new RlpString(childHashBytes)
//    };

//    return Rlp.Encode(rlpNode);
//}

//    /// <summary>
//    /// RLP encodes a branch node.
//    /// </summary>
//public static byte[] RlpEncodeBranchNode(GpuMptNode node, GpuMptContext context, ulong[][] childHashes)
//{
//    List<byte[]> elements = new List<byte[]>(17);

//    unsafe
//    {
//        for (int i = 0; i < 16; i++)
//        {
//            int childIdx = node.Children[i];
//            if (childIdx != -1)
//            {
//                byte[] childHashBytes = new byte[32];
//                for (int j = 0; j < 32; j++)
//                {
//                    childHashBytes[j] = (byte)(childHashes[i][j / 8] >> (8 * (7 - (j % 8))));
//                }
//                elements.Add(childHashBytes);
//            }
//            else
//            {
//                elements.Add(Array.Empty<byte>());
//            }
//        }

//        // The value at the 17th position (value node)
//        if (node.ValueIndex != -1)
//        {
//            // Get the value
//            int valueIndex = node.ValueIndex;
//            int valueStartOffset = valueIndex == 0 ? 0 : context.ValueOffsets[valueIndex - 1];
//            int valueEndOffset = context.ValueOffsets[valueIndex];
//            int valueLength = valueEndOffset - valueStartOffset;

//            byte[] value = new byte[valueLength];
//            for (int i = 0; i < valueLength; i++)
//            {
//                value[i] = context.Values[valueStartOffset + i];
//            }

//            elements.Add(value);
//        }
//        else
//        {
//            elements.Add(Array.Empty<byte>());
//        }
//    }

//    // RLP encode the branch node
//    RlpList rlpNode = new RlpList();
//    foreach (var element in elements)
//    {
//        rlpNode.Add(new RlpString(element));
//    }

//    return Rlp.Encode(rlpNode);
//}
//    public static byte[] HexPrefixEncode(byte[] keyFragment, bool terminator)
//{
//    int flags = 0;
//    int length = keyFragment.Length * 2; // Number of nibbles

//    if (length % 2 == 1)
//    {
//        flags |= 0x1; // Odd length
//    }
//    if (terminator)
//    {
//        flags |= 0x2; // Terminator flag
//    }

//    List<byte> encoded = new List<byte>();

//    if (length % 2 == 1)
//    {
//        // Odd length
//        byte firstNibble = (byte)(flags << 4 | (keyFragment[0] >> 4));
//        encoded.Add(firstNibble);
//        encoded.Add((byte)(keyFragment[0] << 4 | (keyFragment[1] >> 4)));
//        encoded.AddRange(keyFragment.Skip(1));
//    }
//    else
//    {
//        // Even length
//        byte firstByte = (byte)(flags << 4);
//        encoded.Add(firstByte);
//        encoded.AddRange(keyFragment);
//    }

//    return encoded.ToArray();
//}
//    /// <summary>
//    /// Computes the Keccak-256 hash of the input data.
//    /// </summary>
//    public static ulong[] Keccak256(byte[] input)
//    {
//        // Placeholder implementation; in practice, use a GPU-optimized Keccak-256 hash function
//        ulong[] hash = new ulong[4];
//        // Compute hash...
//        return hash;
//    }
//}

///// <summary>
///// Context containing all the data required by the GPU kernels.
///// </summary>
//public struct GpuMptContext
//{
//    // Node-related data
//    public ArrayView<GpuMptNode> Nodes;
//    public ArrayView<int> NodeCount; // Used for atomic node index allocation

//    // Key and value data
//    public ArrayView<byte> Keys;
//    public ArrayView<int> KeyOffsets; // Length: KeyCount
//    public int KeyCount;

//    public ArrayView<byte> Values;
//    public ArrayView<int> ValueOffsets; // Length: ValueCount
//    public int ValueCount;

//    // Level-related data for hash computation
//    public ArrayView<int> LevelStarts; // Starting index of nodes at each level
//    public ArrayView<int> LevelEnds;   // Ending index of nodes at each level
//    public int Levels;                 // Total number of levels in the trie

//    // Current level being processed
//    public int CurrentLevel;
//}


///// <summary>
///// Represents a node in the GPU-based Merkle Patricia Trie.
///// </summary>
//public unsafe struct GpuMptNode
//{
//    public fixed int Children[17]; // Indices to child nodes (16 nibbles + value)
//    public int KeyFragmentOffset;  // Offset to the key fragment (for extension/leaf nodes)
//    public int KeyFragmentLength;  // Length of the key fragment (in nibbles)
//    public int ValueIndex;         // Index into ValueOffsets array (-1 if not a leaf)
//    public int NodeType;           // 0: Branch, 1: Extension, 2: Leaf
//    public fixed ulong Hash[4];    // Store the hash directly (256 bits)
//    public int IsProcessed;        // Flag indicating if the node's hash has been computed
//}
