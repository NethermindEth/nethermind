---
paths:
  - "src/Nethermind/Nethermind.Network/**/*.cs"
  - "src/Nethermind/Nethermind.Network.Discovery/**/*.cs"
  - "src/Nethermind/Nethermind.Network.Dns/**/*.cs"
  - "src/Nethermind/Nethermind.Network.Enr/**/*.cs"
---

# Nethermind.Network

devp2p networking: peer management, protocol handlers, message serialization, and RLPx transport.

Key classes:
- `ProtocolsManager` — coordinates protocol negotiation and handler registration
- `PeerManager` / `PeerPool` — lifecycle of peer connections
- `IMessageSerializer<T>` / `IZeroMessageSerializer<T>` — message encoding/decoding

## Message serializers — IZeroMessageSerializer<T>

All new message serializers must implement `IZeroMessageSerializer<T>` (zero-allocation write path), not the older allocating `IMessageSerializer<T>`:

```csharp
public class MyMessageSerializer : IZeroMessageSerializer<MyMessage>
{
    public void Serialize(IByteBuffer output, MyMessage message)
    {
        // Write directly into the buffer — no intermediate byte[]
        NettyRlpStream stream = new(output);
        stream.StartSequence(GetLength(message));
        stream.Encode(message.Field1);
        stream.Encode(message.Field2);
    }

    public MyMessage Deserialize(IByteBuffer input)
    {
        NettyRlpStream stream = new(input);
        stream.ReadSequenceLength();
        return new MyMessage(stream.DecodeInt(), stream.DecodeHash256());
    }
}
```

- Never allocate `byte[]` inside `Serialize` — write directly to the provided `IByteBuffer`.
- Deserialize should be allocation-minimal; avoid `ToArray()` on spans where possible.

## Protocol versioning

Each Eth protocol version has its own serializer set:

| Protocol | Versions | Registered in |
|----------|----------|---------------|
| `Eth` | Eth62–Eth69 | `NetworkModule` |
| `Snap` | Snap1 | `NetworkModule` |
| `Witness` | Wit0 | `NetworkModule` |

When adding a new protocol version:
1. Create message types in `P2P/<Protocol>/Messages/`
2. Create serializers in `P2P/<Protocol>/` — one serializer per message type
3. Register all serializers in `NetworkModule` using `AddKeyedSingleton<IZeroMessageSerializer<T>>(...)`
4. Never reuse message classes across protocol versions — each version may evolve independently

## P2P session lifecycle

```
ISession.SessionEstablished
    → ProtocolsManager.OnSessionCreated
    → Capability negotiation (Hello message)
    → Protocol handler activation (IZeroProtocolHandler)
    → IZeroProtocolHandler.HandleMessage per incoming message
```

- Implement `IZeroProtocolHandler` for a new protocol; register via `ProtocolsManager`.
- Never block the Netty I/O thread inside `HandleMessage` — dispatch CPU-heavy work to a background scheduler.
- Session teardown: `ISession.Disconnected` event — clean up subscriptions, release resources.

## Peer management

- `PeerPool` tracks all known peers (connected and disconnected).
- `PeerManager` drives connect/disconnect decisions using `IPeerSelectionStrategy`.
- To influence peer selection (e.g., prioritize peers by capability), implement `IPeerSelectionStrategy` — don't modify `PeerManager` directly.
- Static nodes (`StaticNodes/`) are always kept connected; trusted nodes (`TrustedNodes/`) skip peer reputation checks.

## Subdirectories

- `P2P/` — protocol handler base classes and Hello/Disconnect message handling
- `Rlpx/` — RLPx framing, handshake (`RlpxPeer`, `IHandshakeService`)
- `Discovery/` — node discovery (separate module `Nethermind.Network.Discovery`)
- `Config/` — `INetworkConfig`, `ISyncConfig` (network-facing parts)
- `StaticNodes/` — static node management and persistence
- `IP/` — external IP resolution
