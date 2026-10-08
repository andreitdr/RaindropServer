# Wire format

There is one: **protobuf over gRPC**, defined in [RaindropProto](../../RaindropProto/README.md)
(`raindrop/v1/*.proto`). Every request, response, event and error a client sees is a protobuf message,
carried over HTTP/2 inside TLS 1.3. There is no JSON API, and no MessagePack either; the server once
negotiated both, and both are gone.

What the encoding looks like is RaindropProto's business; its
[conventions](../../RaindropProto/docs/conventions.md) set the rules both sides follow. In summary:

- **UUIDs travel as canonical lower-case strings.** Device certificates and attestations sign ids as
  text, so the exact text matters.
- **Times are `google.protobuf.Timestamp`.** The server reads its stores' times as UTC (`Wire.Time`).
- **Opaque crypto blobs are `bytes`.** That covers MLS messages, certificates, signatures, backups and
  ciphertext hashes. The server stores and relays them without parsing them, apart from the MLS
  cleartext framing it does read ([E2EE](e2ee.md)).
- **Open sets stay strings, closed sets are enums.**
  - Strings: permissions, channel types, voice leave reasons, transport names, capabilities and
    plugin event types. A plugin or a newer build can extend these, and a client ignores what it does
    not know.
  - Enums: presence, user state, role kind, attachment kind and upload state. Each has an
    `*_UNSPECIFIED = 0` that an unknown value decodes to.
- **Errors are statuses, not bodies.** See [gRPC services](grpc-services.md#errors).
- **Events are one `oneof`.** See [Events](events.md).

## What is not protobuf, and why

Three things outside the client protocol are not protobuf. Each is fixed by a format someone else owns:

| What | Encoding | Why |
|---|---|---|
| Voice signalling relay (`/api/channels/voice/signal`) | LiveKit's WebSocket protocol, protobuf inside | LiveKit's client SDK speaks it; Raindrop forwards it unread |
| Twirp **errors** from the LiveKit sidecar | JSON `{"code","msg"}` | the Twirp spec writes errors as JSON whatever the request was; requests and answers are binary protobuf |
| LiveKit access tokens | JWT (base64url JSON) | that is what a JWT is |

`raindrop.json`, the owner's configuration file, is JSON too, but it is a file on disk, not traffic.

## Inside an encrypted message

The plaintext of an end-to-end encrypted message is the client's business; the server never sees it.
It is protobuf as well: RaindropProto's `payload.proto`, `MessagePayload`, behind a `0x02` version byte.
Older clients wrote MessagePack, and clients still read that. Neither changes anything here: the
server stores the MLS ciphertext around it and checks attestations, whose bytes are built from fields,
not from this encoding ([Messages](messages.md)).
