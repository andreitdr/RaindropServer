# Transport security

Everything is TLS. `ServerHost` configures Kestrel with the server's certificate and enables both
HTTP/1.1 and HTTP/2, negotiated by ALPN.

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

## Two consequences

1. **The certificate file must persist.** Regenerating it changes the fingerprint and locks out every
   client that pinned the old one. In the Docker setup it lives on a mounted volume for exactly this
   reason.
2. **Nothing sensitive goes in a URL.** Lookups that would naturally be GETs with query parameters
   (`status`, `username-available`, `lookup`) are instead POSTs carrying a body, so UUIDs and
   usernames stay inside the encrypted request body rather than in a URL that tends to end up in
   access logs.

## What this is *not*

This is **transport encryption**: the server can read everything it stores and relays. End-to-end
encryption, where the server owner cannot read messages either, is a separate layer that would sit on
top of this and does not exist yet.

The same goes for **voice**. A client's LiveKit signalling connection is relayed through this server
(`/api/channels/voice/signal/rtc`), so it rides the same pinned certificate and no second certificate
is needed; the audio itself is encrypted with DTLS-SRTP, but only as far as the LiveKit server, which
decrypts it to forward it to everybody else in the call. Whoever runs the server can, in principle,
listen in. See [Voice](voice.md#the-signalling-relay).

## Configuration knobs

Set `tls.enabled` to `false` only when a reverse proxy terminates TLS in front of the server, and
listen on `http://` in that case — an `https://` URL with TLS switched off is treated as a startup
configuration error rather than a silent downgrade to plaintext. See [Configuration](configuration.md)
for the full `tls` section and the `RAINDROP_TLS_*` environment variable overrides.
