# Lab3: A local network chat

A chat for the local network on UDP sockets, in C#, with no server: every running copy is a peer. The general
channel is one multicast datagram for everybody; private messages go to one peer only and are encrypted with a
key derived from a common passphrase.

Only the `Socket` class with `SocketType.Dgram` is used (`Bind`, `SendTo`, `ReceiveFrom`, `SetSocketOption`,
`Close`): no `UdpClient` and no other library.

| Folder | Contents |
|--------|----------|
| `Chat/` | the chat (C#) |
| `python/` | `lanchat.py`: a second peer of the same chat, in Python (bonus: two languages talking to each other) |
| `tests/` | test programs in Python |

## Building and running

Requires the .NET 8 SDK or newer (it also runs on .NET 10). From the `Lab3` folder:

```
dotnet build -c Release Chat
dotnet run -c Release --project Chat -- <name> <port> <passphrase>
```

Every peer of one chat uses the same port and the same passphrase, for example in three terminals:

```
dotnet run -c Release --project Chat -- Ana 5050 secret
dotnet run -c Release --project Chat -- Ion 5050 secret
dotnet run -c Release --project Chat -- Maria 5050 secret
```

Several peers can run on the same computer, and on different computers in the same network. The network must let
multicast through (many Wi-Fi networks do not: then use the hotspot of a phone), and the firewall must allow UDP on
the chat port and on the peers' own ports.

The Python peer needs the `cryptography` package (`pip install cryptography`):

```
python3 python/lanchat.py Py 5050 secret
```

## Commands

| Command | What it does |
|---------|--------------|
| `text` | sends text to everybody (the general channel) |
| `/msg name text` | sends text to name only (a private message) |
| `/users` | shows who is online, with their endpoints |
| `/quit` | leaves the chat |

A message has at most 500 characters.

## How it works

Every peer has two sockets:

- the **group socket**, bound to `0.0.0.0:<port>` with `ReuseAddress` (so several peers on one computer can share
  the port) and joined to the multicast group `239.255.42.99`: it receives the general channel and the
  announcements;
- the **direct socket**, bound to `0.0.0.0:0` (a port of its own): every datagram of the peer is sent from it, so
  this port is the peer's endpoint, and private messages, answers and acknowledgements arrive on it.

Joining: a new peer sends `hello` to the group; every peer online answers `here` directly to it, or `taken` if it
has the same name. After one second without `taken` the peer sends `join`. Every 5 s each peer sends `alive`; a
peer silent for 15 s is considered gone; `/quit` sends `bye`. If two peers start with the same name at the same
moment, the one with the smaller id wins.

Private messages are sent to the receiver's endpoint and confirmed with `ack`; without an ack within 1 s the
message is sent again, at most 3 times. Their text is encrypted with AES-256-GCM, with a key derived from the
passphrase with PBKDF2-HMAC-SHA256 (200 000 iterations, salt `lanchat/1:<port>`), a random 12-byte nonce per
message, and the fields `id|name|to|seq` as associated data.

Each peer writes `chat-<name>-<port>.log` in the current folder: one line for every datagram sent, received or
dropped, with the time, the endpoint, the type and the size. Invalid datagrams (not JSON, another protocol, unknown
type, bad fields, text over 500 characters, more than 4096 bytes, a private message that does not decrypt) are
dropped and logged.

## Message format

Every datagram is one JSON object (UTF-8). Fields: `chat` (always `"lanchat/1"`), `type`, `id` (16 lowercase hex
digits, random per peer), `name` (1 to 20 of `A-Z a-z 0-9 _ -`), and depending on the type:

| type | sent to | extra fields |
|------|---------|--------------|
| `hello`, `join`, `alive`, `bye` | group | – |
| `here`, `taken` | the new peer | – |
| `general` | group | `text` |
| `private` | one peer | `to`, `seq`, `nonce` (Base64, 12 bytes), `data` (Base64: ciphertext + 16-byte tag) |
| `ack` | the sender of a private message | `seq` |

```
{"chat":"lanchat/1","type":"general","id":"8c0528d6618516c9","name":"Ana","text":"hello everyone"}
```

## Tests

| Script | What it does |
|--------|--------------|
| `tests/garbage.py <port>` | sends invalid datagrams and one valid message to the group |
| `tests/outsider.py <port> <seconds>` | joins as Eve with a wrong passphrase and shows the private datagrams it receives |
| `tests/tamper.py <port> <passphrase> <name>` | sends a private message, then changed copies and a replay, to one peer |

The report with all the results is `Lab3_IonMorosanu_FAF-242.pdf` (and `.docx`).
