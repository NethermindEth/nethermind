# Browser WebTransport

Nethermind can accept signed Ethereum transactions over the experimental `webtx/1`
protocol on HTTP/3. The listener is disabled by default.

The WebTransport server runs in Nethermind and uses the platform MsQuic library.
Windows requires a QUIC-capable .NET runtime. Linux requires `libmsquic` 2.5 or later.
See the [.NET QUIC platform requirements](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview).

Start Nethermind with `--Network.WebTransportPort 30304` and allow inbound UDP on
that port. The P2P TCP and discovery UDP ports both default to 30303. QUIC can
use UDP 30303 if discovery uses another UDP port or is disabled. QUIC and discovery
use separate sockets. Configure any UDP forwarding at the firewall or NAT.

Retrieve the node's signed `enr` from `admin_nodeInfo` on a trusted local RPC
endpoint with the `Admin` module enabled. Publish a fresh signed ENR to give
browser clients an initial peer. The ENR carries `quic` and/or `quic6` for the
UDP endpoint and `qh` for the current
and next SHA-256 TLS certificate hashes. Serve a current ENR from the bootstrap
source or refresh its published record at least once per seven-day rotation. A
restart creates a fresh certificate pair and requires an immediate refresh.

## Wire protocol

Use a browser that supports the WebTransport `protocols` and `serverCertificateHashes`
options and the HTTP/3 WebTransport draft-02 negotiation. Leave `allowPooling`
disabled. The listener serves one WebTransport session per QUIC connection.
From a trusted or verified ENR, select an IP and its matching QUIC port and
connect to `https://<ip>:<port>/devp2p`. Supply each 32-byte `qh` pin as a
`{ algorithm: "sha-256", value: pin }` entry, and offer the desired capability and
`ENR-key-proof-v1=<hex nonce>` in `protocols`. Generate a fresh random 32-byte nonce
for each session. Enclose IPv6 addresses in square brackets in the URL.

The server opens a bidirectional stream and sends an unframed RLP handshake
`[proof, [capability]]`. Verify the 65-byte recoverable signature with the node key
from the ENR. The signed digest is Keccak-256 of the ASCII string `ENR-key-proof-v1`
followed by the raw nonce. For each subsequent frame, the first byte is the message
code, the next three bytes contain the big-endian compressed size, and the payload
is the raw Snappy block. The server sends one reply per session and finishes its
send side. Close the client's send side and then the session after reading the result.

- `webtx/1` accepts one `0x10` message with an RLP list of up to 16 signed transactions
  and a decoded size of at most 256 KiB. Use a legacy RLP list or an RLP string
  around the EIP-2718 typed envelope. The `0x11` reply is an RLP list with one
  `[resultCode]` entry per transaction in submission order. Code 0 means the
  transaction entered the local pool. Codes 1–8 use Geth's `webtx` result categories.
  Pool rejections without a more specific local result use code 8.
- `webdisc/1` is an experimental Nethermind extension. Send `0x10` with an empty
  RLP list (`c0`) to request up to 16 signed ENRs. The `0x11` response contains those
  records as a list of RLP items. The listener uses its own signed record and
  verified QUIC-capable records it knows from peer discovery. A listener bound
  to loopback can return loopback records for local testing. Other listeners relay
  only publicly routable QUIC endpoints. Verify every ENR before dialing it.
  Offer this capability in a separate session from `webtx/1`.

The transport follows the [devp2p QUIC draft](https://github.com/ethereum/devp2p/pull/276)
and Geth's experimental [browser transaction protocol](https://github.com/ethereum/go-ethereum/pull/35950).
