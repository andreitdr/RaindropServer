# Wire formats

Request and response bodies are encoded by an `IPayloadCodec`
([`RaindropServer/Payloads/`](../RaindropServer/Payloads/)), negotiated per request from the
`Content-Type` and `Accept` headers:

| Format | Content type | Notes |
|---|---|---|
| JSON | `application/json` | The default, and what curl sends when you say nothing. |
| MessagePack | `application/x-msgpack` | Binary; what a real client should use. |

The two are interchangeable in both directions — a request can arrive as JSON and be answered in
MessagePack — and error bodies (`{ "error", "message" }`) follow the same negotiation.
`GET /api/server/info` advertises which formats a given server supports, so a client can check before
committing to one.

The same negotiation picks the format of the [event stream](events.md): `Accept` on the WebSocket
upgrade decides it, and JSON is sent in text frames, MessagePack in binary ones. A codec says which it
is through `IPayloadCodec.IsBinary`, and `Encode<T>` encodes a value on its own — no `HttpResponse`
involved — so one event is encoded once per format however many connections hear it. JSON uses the
same web defaults (`JsonSerializerOptions.Web`) as response bodies, so an event is spelled exactly like
the same object in a response.

## MessagePack specifics

MessagePack is serialized **contractless**, so the C# request/response records stay plain records with
no serialization attributes. The trade-off: binary keys are the property names verbatim (`UserId`)
where JSON camelCases them (`userId`). A .NET client sharing the contract types never notices; a
client written in another language needs to know this to decode the binary form correctly.

Typeless resolvers are deliberately not used, and deserialization runs under
`MessagePackSecurity.UntrustedData` — i.e. it's hardened against a malicious payload trying to trigger
arbitrary type construction.

## Is the binary format actually worth it?

Worth being honest about the payoff, per the project's own design notes: on the small objects user
management deals with, MessagePack saves only about 15% of the bytes — GUIDs dominate the payload
size, and the longer PascalCase keys eat into the gain. The wins that actually matter are:

- skipping text parsing entirely,
- HTTP/2 multiplexing every call over one connection, and
- headroom for message history and voice metadata, where payloads are numeric and repetitive and
  binary pulls far ahead of JSON.

`RaindropServer` currently only uses JSON by default (`ApiClient` in the Flutter client sends
`application/json` unconditionally) — MessagePack is fully implemented server-side but not yet adopted
client-side; see the client's [wire format doc](../../RaindropClient/docs/wire-format.md).
