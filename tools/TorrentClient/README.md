# Nethermind Torrent Client

The standalone .NET client downloads and SHA-1 verifies BitTorrent v1 payloads, then
shares verified pieces with other peers. Windows MAUI and Linux GTK desktop heads
also keep completed torrents available for seeding while they are running.

## CLI

```powershell
dotnet run --project tools/TorrentClient/src/Nethermind.Torrent.Cli -c Release -- "C:\Users\flcl\Downloads\slackware64-14.2-install-dvd.torrent" --output "D:\Downloads" --port 6881
```

The process remains online after the payload is complete or restored from disk.
Press Ctrl+C to stop and send a tracker `stopped` event. Use `--no-seed` to exit
after verification/download. `--upload-slots 8` controls simultaneous
upload peers; `--port 0` selects an available port, but a fixed forwarded TCP
port is more useful for external peers. If a configured port is unavailable,
the client logs the fallback port and advertises that actual port. The desktop
Peers tab also shows the active listening port. `--help` lists the remaining
options.

The CLI also accepts v1 `magnet:?xt=urn:btih:...` links and BEP 46
`magnet:?xs=urn:btpk:<64 hex public key>&s=<optional hex salt>` feeds. It
resolves the current feed pointer through the mainline DHT, verifies the BEP 44
signature, downloads BEP 9 metadata, and caches the resulting `.torrent` under
the output directory's `.metainfo` folder. The CLI resolves a feed once at
startup; restart it to check for a newer pointer. BEP 46 requires DHT to be
enabled.

The desktop client retains a BEP 46 feed URI and highest accepted sequence in
its queue and checks active feeds every 15 minutes. A new signed infohash adds
a new torrent job; the older job remains available for seeding. It does not
delete or overwrite previously downloaded files. Each feed version is stored
under a separate `.bep46` directory below the chosen download root. BEP 44 immutable and mutable
get/put are available through `DhtItemClient`; `Bep46Link.SignUpdate` creates a
signed feed pointer for publication with `PutMutableAsync`. DHT items may expire
unless republished, and DHT availability is not guaranteed.

Only SHA-1-verified pieces are advertised and served. Seeds accept inbound TCP
peers, connect to reachable peers discovered through trackers or DHT, and serve
the original info dictionary to magnet peers through `ut_metadata`. Tracker and
mainline-DHT announcements publish the active listening port; DHT announcements
are refreshed while seeding. Private torrents never use public DHT discovery or announcements.
The client does not currently configure firewall or NAT port
forwarding, so outbound discovery alone does not guarantee internet peers can
connect to a private host. Keep the app running and allow/forward its TCP port
to seed outside the local network. Payload transfers currently use TCP; the
uTP packet primitives are not connected to the transfer path.
