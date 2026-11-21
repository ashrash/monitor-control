# Peer protocol

How two Monitor Switcher installations that share a monitor ask each other to act. Used when the
monitor only obeys the computer it is showing (profile quirk `ignoresCommandsWhenInactive`, for
example the LG 32UN880): the computer that is not on screen asks the one that is.

Implemented in `windows/src/MonitorSwitcher.Core/Peer` and `macos/Sources/MonitorSwitcherCore/Peer`
(messages, signing, relay decisions), with the UDP transport in `windows/src/MonitorSwitcher.Windows/Peer`
and `macos/Sources/MonitorSwitcherMac/PeerUdp.swift`. Both test suites check
[`peer-vectors.json`](peer-vectors.json), which an independent implementation generated.

## Pairing

Both computers store the same pairing code in `settings.json`:

```json
"peer": { "pairingCode": "K7QF-2M9X-HC4T-8WPA-R3NB", "port": 47823 }
```

- The code is 20 characters from `ABCDEFGHJKMNPQRSTUVWXYZ23456789` (no I, L, O, 0 or 1), about 99 bits.
  Settings generates one; it is typed into the other computer.
- Normalising: upper case, spaces and dashes removed. 16 to 64 characters from the alphabet, else invalid.
- Key: `SHA-256("MonitorSwitcher pairing v1\n" + normalised code)`.
- `port` is optional (default 47823, UDP). Both computers must use the same port.

## Datagrams

One UDP datagram per message, at most 8192 bytes, UTF-8:

```
MSW1 <HMAC as 64 lower-case hex digits> <JSON>
```

`HMAC = HMAC-SHA256(key, "MSW1 request\n" + JSON bytes)` for requests and
`"MSW1 reply\n" + JSON bytes` for replies. The HMAC covers the JSON bytes exactly as sent, so the
two implementations never need to serialise JSON identically. The label means a reply can't be
replayed as a request. A datagram that fails the check is dropped without an answer.

### Request

| Field | Type | |
|---|---|---|
| `v` | int | 1 |
| `id` | string | 32 random hex digits; replies carry it back |
| `ts` | int | Unix time in seconds |
| `machine` | string | Random id of the sending computer (kept in `state.json`) |
| `from` | string | Sending computer's name, for messages |
| `kind` | string | `ping`, `switch` or `control` |
| `monitor` | object | `switch`/`control`: `manufacturerId`, `productCode`, `serialNumber`, optional `serialString`, `profileId` |
| `inputId` | string | `switch`: an input id from the profile, such as `usbc` |
| `control` | string | `control`: `Brightness` or `Volume` |
| `setPercent` / `adjustPercent` | int | `control`: 0 to 100, or -100 to +100 |

### Reply

`v`, `id` (the request's), `from`, `ok` (bool), `message` (shown to the user), and when relevant
`inputId`, `inputName`, `percent`.

## Sending

The sender sends the request to the address the peer last answered from (`state.json` `peer.lastAddress`)
and broadcasts it on every local IPv4 network (directed broadcast addresses and 255.255.255.255).
It resends every 600 ms until a reply with the same `id` arrives, or gives up after 4 seconds.

## Receiving

The receiver, which is the menu bar or tray app, listens on the UDP port. For each datagram it:

1. drops it unless the HMAC is right;
2. drops it if `machine` is its own id (its own broadcast);
3. resends the cached reply if it has already handled this `id` (retries never act twice);
4. refuses it if `ts` is more than 120 seconds from its own clock (the reply says so);
5. otherwise carries it out and replies to the sender's address and port.

Carrying out a request is the same as a local action, with the receiver's own profiles and
safety checks. The monitor is found by manufacturer and serial number, because the two
computers can see different product codes for the same monitor (the 32UN880 reports 0x774F over
HDMI and 0x7750 over USB-C). The profile id is used only when there is no serial. Inputs must
be in the receiver's profile, and controls are limited to brightness (VCP 0x10) and volume
(VCP 0x62). A request is never relayed again.

## When a computer relays

Only when all of these are true: it is paired, the monitor's profile has
`ignoresCommandsWhenInactive`, and the monitor does not answer a DDC/CI read from this computer
(two tries). In that case:

- switching to an input asks the peer to switch to it;
- toggle asks the peer to switch to **this** computer's input ("come back here");
- brightness and volume changes are made by the peer.
