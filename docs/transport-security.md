# Transport security

Everything is TLS. `ServerHost` configures Kestrel with the server's certificate and enables both
HTTP/1.1 and HTTP/2, negotiated by ALPN.

## Protocol versions

The server accepts **TLS 1.3 only**. TLS 1.3 removes every legacy cipher and always provides forward
secrecy. Set `tls.allowTls12` to also accept 1.2 for an older client you cannot update. On Linux, TLS 1.2
is then limited to ECDHE key exchange with AEAD ciphers (AES-GCM, ChaCha20-Poly1305), matching what 1.3
guarantees.

**Exception on macOS:** .NET serves TLS there through Apple's SecureTransport, which cannot serve TLS
1.3. On macOS the server allows 1.2 and 1.3 and says so at startup. For a 1.3-only server, run it on
Linux (for example the Docker image) or on Windows.

A generated certificate is ECDSA P-256. An existing RSA certificate on disk keeps loading unchanged.

## Self-signed certificates and fingerprint pinning

Raindrop servers are self-hosted on whatever address their owner has, and most will never have a
CA-issued certificate. [`ServerCertificate`](../RaindropServer/Security/ServerCertificate.cs) generates
a self-signed one on first start and saves it. That encrypts the connection but proves nothing about
who is on the other end — so **clients are expected to pin the fingerprint**:

- A client already stores a per-server GUID (see the client's
  [connecting-to-a-server doc](../../RaindropClient/docs/connecting-to-a-server.md)).
- It stores the server's SHA-256 fingerprint alongside that GUID the first time it connects
  (trust-on-first-use).
- It refuses to continue if the fingerprint ever changes.

This is exactly the same trust model SSH uses for host keys. Point `tls.certificatePath` at a
CA-issued PFX instead (see [Configuration](configuration.md)) and clients can verify it normally,
without needing to pin anything.

The fingerprint is:

- printed at server startup,
- available from the `fingerprint` console command (see [Configuration](configuration.md#the-owner-console)),
- and served by `GET /api/server/info`.

## The server identity key: changing the certificate without breaking pins

A certificate pinned on first use would lock every client out the moment it changed. The server
therefore also keeps a long-lived **identity key**, an ECDSA P-256 key stored in `server-identity.key`
(owner-only permissions, generated on first start, path set by `tls.identityKeyPath`). Its
[`ServerIdentityKey`](../RaindropServer/Security/ServerIdentityKey.cs) signs the fingerprint of whatever
certificate the server presents. `GET /api/server/info` returns:

| Field | Contents |
|---|---|
| `identityKey` | Base64 of the uncompressed public point (`0x04‖X‖Y`, 65 bytes) |
| `tlsBindingSignature` | Base64 of an ECDSA-SHA256 signature, `r‖s` (64 bytes), over `"raindrop/tls-binding/v1\n"` + the fingerprint in lower-case hex without separators |
| `capabilities` | Includes `tls.identity-binding.v1` when the above are present |

A client pins the identity key next to the fingerprint. If the fingerprint later stops matching, the
client makes one unauthenticated request to `/api/server/info` and accepts the new certificate only if
the pinned identity key signed the certificate that the TLS handshake actually presented. Otherwise the
mismatch stands.

To replace the certificate (renewal, moving to a CA-issued one, recovering from a leaked TLS key),
replace `raindrop.pfx` and restart. The server re-signs the new fingerprint at startup. The console's
`fingerprint` command prints both fingerprints.

## Consequences

1. **The identity key must persist, and should be backed up.** Losing it makes the server a stranger
   to every client, just as losing the certificate did before identity keys existed. The certificate
   itself can now be replaced. In the Docker setup, both files live on a mounted volume.
2. **Nothing sensitive goes in a URL.** Lookups that would naturally be GETs with query parameters
   (`status`, `username-available`, `lookup`) are instead POSTs carrying a body, so UUIDs and
   usernames stay inside the encrypted request body rather than in a URL that tends to end up in
   access logs.

## LiveKit

Raindrop reaches LiveKit's API over `voice.livekit.apiUrl`. Plain `http://` is accepted only for a
loopback address or a single-label host name (a Docker service such as `livekit`). Anything further
away must be `https://`. The API secret must be at least 32 characters, and LiveKit's `--dev` secret is
refused unless `voice.livekit.allowWeakSecret` is set for a throwaway local server. `docker-compose.yml`
runs LiveKit from [`livekit.yaml`](../livekit.yaml) with its keys taken from `.env`, not in `--dev`
mode. That file also has a commented-out TURN-over-TLS section for clients on networks that block UDP.

## What this is *not*

This is **transport encryption**. End-to-end encryption, where the server owner cannot read messages or
hear calls either, is a layer on top of it. Messages and calls are both end-to-end encrypted (see
[E2EE](e2ee.md)); what the server can still read is metadata, and messages from before the switch.

The same two layers apply to **voice**. A client's LiveKit signalling connection is relayed through this
server (`/api/channels/voice/signal/rtc`), so it rides the same pinned certificate and no second
certificate is needed. The audio is DTLS-SRTP to the LiveKit server, and inside that, each frame is
encrypted end to end with a key from the voice channel's MLS group. LiveKit forwards frames it cannot
decrypt. See [Voice](voice.md#end-to-end-encrypted-calls).

## Configuration knobs

Set `tls.enabled` to `false` only when a reverse proxy terminates TLS in front of the server, and
listen on `http://` in that case — an `https://` URL with TLS switched off is treated as a startup
configuration error rather than a silent downgrade to plaintext. See [Configuration](configuration.md)
for the full `tls` section and the `RAINDROP_TLS_*` environment variable overrides.
