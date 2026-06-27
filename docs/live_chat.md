# Live Chat WS Schema

This document defines the websocket contract for the live chat server implemented in
[services/chat-ws.js](../services/chat-ws.js). It is a **separate** WebSocket server from the
external-data WS — the same clients open a second connection to it.

- **Transport:** plain WebSocket (no subprotocol). All frames are UTF-8 JSON text.
- **Endpoint:** `ws://<CHAT_WS_HOST>:<CHAT_WS_PORT>` (defaults `0.0.0.0:3200`).
- **Toggle:** the server only runs when `CHAT_WS_ENABLED` is unset or truthy. Set
  `CHAT_WS_ENABLED=false` (or `0`/`off`/`no`/`disabled`) to turn it off.
- **Protocol id:** `chat-ws/v1` (sent in the `hello` frame).

Every message is a JSON object with a `type` field. Unknown types receive an `error`.

---

## Connection lifecycle

1. Client connects → server immediately sends [`hello`](#hello).
2. Client sends [`join`](#join) with a username.
3. On success the server sends [`joined`](#joined), then [`history`](#history), then
   [`presence`](#presence) (to the joining client), and broadcasts an updated
   [`presence`](#presence) + a [`system`](#system) `user_joined` to everyone else.
4. Client may now send [`message`](#message-client--server) frames; the server broadcasts
   each as a [`message`](#message-server--client) to all connected clients.
5. On disconnect (or [`leave`](#leave)) the server broadcasts updated
   [`presence`](#presence) + a [`system`](#system) `user_left`.

A client **must** `join` before sending chat messages; otherwise it gets an `error` with
code `not_joined`.

---

## Client → Server messages

### join
Claim a username and enter the room. The `username` is the player's **FFXIV character name**;
the server resolves it to a server member before admitting the client (see
[Username resolution](#username-resolution)).
```json
{ "type": "join", "username": "Tataru Taru" }
```
- The name must resolve to exactly one server member. Unknown or ambiguous names are rejected
  with code `username_unresolved`.
- Both FC members and FC friends may join. The resolved FC status is returned on `joined` and
  shown in `presence`/`message`.
- One live connection per resolved member. A second connection for a member already online is
  rejected with code `username_taken`.
- The displayed name in `joined`/`presence`/`message` is the **resolved** canonical name, which
  may differ in casing/spacing from what was sent.

### message (client → server)
Send a chat message. Requires a prior successful `join`.
```json
{ "type": "message", "text": "Hello Eorzea!" }
```
The alternate nested form is also accepted: `{ "type": "message", "payload": { "text": "..." } }`.
- `text` is trimmed; empty text is rejected (`invalid_message`).
- Max length `2000` chars (`invalid_message` if exceeded).
- Flood control: max `8` messages per `10s` per connection (`rate_limited` if exceeded).

### get_history
Request the recent message history on demand (also pushed automatically on join).
```json
{ "type": "get_history" }
```

### get_presence
Request the current online-user list on demand.
```json
{ "type": "get_presence" }
```

### leave
Voluntarily leave the room without closing the socket.
```json
{ "type": "leave" }
```

### ping
Application-level keepalive.
```json
{ "type": "ping" }
```
> Note: the server also runs WebSocket-level ping/pong heartbeats every 30s and terminates
> unresponsive sockets. Browser `WebSocket` clients answer those automatically.

---

## Server → Client messages

### hello
Sent once, immediately on connect.
```json
{
  "type": "hello",
  "protocol": "chat-ws/v1",
  "limits": {
    "usernameMinLength": 3,
    "usernameMaxLength": 32,
    "maxMessageLength": 2000,
    "historyLimit": 50,
    "rateLimit": { "windowMs": 10000, "maxMessages": 8 }
  }
}
```

### joined
Acknowledges a successful `join`. `username` is the resolved canonical name; `isFCMember`
reflects the resolved member's FC status.
```json
{ "type": "joined", "username": "Tataru Taru", "isFCMember": true }
```

### history
Recent messages in **chronological order** (oldest first). Sent automatically after
`joined`, and in response to `get_history`.
```json
{
  "type": "history",
  "messages": [
    { "id": "uuid", "username": "Alphinaud Leveilleur", "isFCMember": true, "text": "wb", "ts": 1719446400000 }
  ]
}
```

### presence
The current set of online users (each with FC status). Sent to the joining client and
broadcast on every join/leave, and in response to `get_presence`.
```json
{
  "type": "presence",
  "users": [
    { "username": "Tataru Taru", "isFCMember": true },
    { "username": "Alphinaud Leveilleur", "isFCMember": false }
  ],
  "count": 2
}
```

### message (server → client)
A chat message broadcast to all clients (including the sender).
```json
{
  "type": "message",
  "message": { "id": "uuid", "username": "Tataru Taru", "isFCMember": true, "text": "Hello Eorzea!", "ts": 1719446400000 }
}
```

### system
Lightweight room notices, broadcast to everyone except the subject.
```json
{ "type": "system", "event": "user_joined", "username": "Tataru", "ts": 1719446400000 }
```
- `event` is one of `user_joined` | `user_left`.

### left
Acknowledges a voluntary `leave`.
```json
{ "type": "left" }
```

### pong
Reply to a client `ping`.
```json
{ "type": "pong", "ts": 1719446400000 }
```

### error
Any rejected request.
```json
{ "type": "error", "code": "username_taken", "message": "Username \"Tataru\" is already in use", "requestType": "join" }
```

| code                    | meaning                                                        |
| ----------------------- | -------------------------------------------------------------- |
| `invalid_json`          | frame was not valid JSON                                       |
| `invalid_username`      | username failed the basic format pre-check (type/length)       |
| `username_unresolved`   | name did not resolve to a unique server member (see `message`) |
| `username_taken`        | the resolved member is already connected                       |
| `resolution_unavailable`| the server has no resolver configured                          |
| `not_joined`            | sent a `message` before joining                                |
| `invalid_message`       | empty or too-long message text                                 |
| `rate_limited`          | exceeded the per-connection message rate                       |
| `unsupported_type`      | unknown `type`                                                 |
| `internal_error`        | unexpected server-side error                                   |

For `username_unresolved`, the `message` field carries the resolver's reason, e.g.
`No Discord member match found for ingameName "..."` or
`Multiple Discord members matched ingameName "...". Please use a unique character name.`

---

## Message object

```json
{
  "id": "string (uuid v4)",
  "username": "string (resolved canonical name)",
  "isFCMember": true,
  "text": "string (1..2000 chars, trimmed)",
  "ts": 1719446400000
}
```
- `ts` is epoch milliseconds (`Date.now()`).
- `isFCMember` is the resolved FC status of the sender.

---

## Username resolution

The chat reuses the **same resolver as the participant submit flow**
(`resolveExternalParticipantByIngameName`). Joining is admitted only when the name resolves
to a single server member:

1. A cheap format pre-check first: must be a string; control characters are stripped, internal
   whitespace collapsed, trimmed; length **3–32** (otherwise `invalid_username`).
2. The cleaned name is then resolved against the FC member cache and the guild's members
   (nickname/character-name matching). The name must match **exactly one** member:
   - zero matches → `username_unresolved` (not found),
   - more than one match → `username_unresolved` (ambiguous — use a unique character name).
3. Both FC members and FC friends are admitted. The resolved member's FC status is returned as
   `isFCMember` and surfaced in `joined`, `presence`, and each `message`.
4. Identity is keyed by the resolved **Discord member id**, so the same member cannot hold two
   live chat connections (`username_taken`), regardless of casing/spacing in the typed name.

---

## Persistence & presence

- **History:** stored in the shared Redis connection under the list key `chat:history`
  (configurable via `CHAT_WS_HISTORY_KEY`). It is capped to the most recent
  `CHAT_WS_HISTORY_LIMIT` (default **50**) messages and the key expires **24h** after the
  last write.
- **Presence:** held in memory and derived from live connections. It resets on bot restart;
  clients simply re-`join` to repopulate it.

---

## Minimal client example

```js
const ws = new WebSocket('ws://localhost:3200');

ws.onmessage = (e) => {
  const msg = JSON.parse(e.data);
  switch (msg.type) {
    // username must be the player's FFXIV character name (resolved server-side)
    case 'hello':    ws.send(JSON.stringify({ type: 'join', username: 'Tataru Taru' })); break;
    case 'joined':   console.log('joined as', msg.username, 'FC:', msg.isFCMember); break;
    case 'history':  msg.messages.forEach(render); break;
    case 'message':  render(msg.message); break;
    case 'presence': renderOnline(msg.users); break; // users: [{ username, isFCMember }]
    case 'error':    console.warn('chat error', msg.code, msg.message); break;
  }
};

function send(text) {
  ws.send(JSON.stringify({ type: 'message', text }));
}
```
