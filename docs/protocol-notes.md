# Protocol and implementation notes

How EufyNet talks to the Eufy cloud and a HomeBase 2, what was observed on real hardware (HomeBase 2 firmware 3.4.2.6h,
eufyCam 2 Pro), and where it is in the code. The user guide is the [README](../README.md).

## Code layout

| Project | What |
|---|---|
| `src/EufyNet.Cloud` | Cloud login (ECDH + AES, 2FA, captcha, trusted device), session persistence, station/device lists |
| `src/EufyNet.P2P` | UDP session, commands, encryption, events, local motion detection, picture decoding |
| `src/EufyNet.Core` | `EufyDriver` (login → stations → P2P, state, commands), `ActivityStore`, and the transport contract (`IBridgeTransport`, `TransportBridge`) |
| `src/EufyNet.Transport.Console` | Transport: events and state as log lines, commands and login answers from stdin |
| `src/EufyNet.Transport.Mqtt` | Transport: MQTT with Home Assistant discovery, events for Node-RED, commands in |
| `src/EufyNet.App` | Generic Host wiring |
| `docker/` | Dockerfile and compose service |
| `tests/EufyNet.Tests` | Crypto checked against Node-generated vectors; login flow against a fake Eufy server; P2P against a fake station; MQTT against an in-process broker |

`nuget.config` limits restore to nuget.org, so machine-wide private feeds are not needed.

`EufyDriver` knows no transport. It raises `LoginChanged`, `StateChanged` and `EventRaised` and executes `BridgeCommand`s
(`status`, `set_guard_mode`, `info`, `clips`, `picture`, and the login answers `verify_code`, `resend_code`, `captcha`).
Each transport gets its own `TransportBridge`, which forwards in order and never lets a slow transport block the driver.

## Cloud login

- The session is saved to `data/cloud-session.json`. It holds the auth token. Later runs restore it without 2FA; `--force-login` ignores it.
- On the first login Eufy emails a code; afterwards the app registers itself as trusted device `eufynet`.
- `data/stations.json` and `data/devices.json` hold the raw decrypted lists (`p2p_did`, `app_conn`, `member`, `params` …).

## P2P connection

After listing, the app keeps a P2P session open to every station until Ctrl+C (`--list-only` skips this).

- `Eufy:P2PConnectionSetup`: `OnlyLocal` (default, LAN only) or `Quickest` (also asks Eufy's cloud lookup servers, using the station's DSK key).
- `Eufy:StationIpAddresses`: station LAN IPs by serial. The cloud's `ip_addr` is the public IP, so set this. Without an entry the lookup is broadcast.
- Flow: `LOCAL_LOOKUP` → `CHECK_CAM` (port ±3) → `CAM_ID` → `PING` every 5 s. Every incoming DATA packet is ACKed. After 50 s without a `PONG` the session ends and reconnects (backoff 5 → 60 s).
- Every 5 minutes it logs a status line. `Logging__LogLevel__EufyNet.P2P=Debug` shows each lookup step and DATA packet.

The code is in `src/EufyNet.P2P`: `P2PPackets` (builders/parsers, checked against Node vectors), `P2PSession` (one connection), `P2PStationConnector` (reconnect loop).

## Commands: encryption and guard mode

Once connected, the app sends `CMD_GATEWAYINFO` and sets up command encryption. It then reads commands from the console:

| Command | What it does |
| --- | --- |
| `status` | guard mode, alarm mode and encryption per station |
| `info` | `CMD_CAMERA_INFO` (read-only); prints the station's JSON |
| `mode <away\|home\|schedule\|custom1..3\|off\|geo\|disarmed> [serial]` | **sets the guard mode** (real alarm change) |
| `quit` | close the sessions and exit |

- Encryption: the station replies with a cloud cipher id plus an RSA-wrapped session key (Level 2). For this account Eufy's `get_ciphers` returns the private key lower-cased, so it cannot be decoded. The app falls back to Level 1 (key from serial + DID), exactly like eufy-security-client. The log shows this as a warning. Encrypted commands work under Level 1, which `info` confirms.
- Guard mode on firmware ≥ 2.0.7.9 goes through `CMD_SET_PAYLOAD` (JSON with `cmd: 1224`); older firmware uses `CMD_SET_ARMING`. The starting value comes from cloud param 1224. Alarm-mode changes the station pushes (`CMD_GET_ALARM_MODE`) are logged.
- Code: `P2PCommands` (framing, AES/RSA, Node vectors), `P2PMessageAssembler` (ordering, reassembly), `P2PCommandChannel` (one command at a time, resend until ACK, result matching), `HomeBaseClient`.

## Events from the HomeBase

Events the station pushes over P2P are logged as `Event: …` lines, for example `Front Door: person detected [event_type 3102]`. `HomeBaseEventParser` (in `src/EufyNet.P2P/HomeBaseEvents.cs`) turns CONTROL messages into transport-neutral `StationEvent` records:

| Message | Record |
| --- | --- |
| `CMD_GET_ALARM_MODE` | `AlarmModeChangedEvent` |
| `CMD_GET_DELAY_ALARM` | `AlarmArmedEvent` (event 0) / `AlarmDelayEvent` |
| `CMD_SET_TONE_FILE` | `AlarmTriggeredEvent` |
| `CMD_GET_TFCARD_STATUS` | `StorageStatusEvent` |
| `CMD_NOTIFY_PAYLOAD` cmd 2037 (`CMD_CAMERA_PUSH_NOTIFY`) | `DetectionEvent` (motion / person / doorbell / pet / vehicle …) |
| `CMD_NOTIFY_PAYLOAD` cmd 1550 / 1829 | `SensorStatusEvent` / `HubNotifyUpdateEvent` |
| anything else | `UnknownEvent` (command, inner cmd, payload), so nothing is dropped silently |

With `Eufy:CaptureEvents=true`, every CONTROL and unsolicited DATA message (except pings) is appended to `data/events.jsonl` (time, command, channel, payload as text and hex). That file is the raw material for new parsers.

### Motion on a HomeBase 2 without the cloud

The capture showed that HB2 (3.4.2.6h) sends no detection push over P2P. When a camera records, two messages arrive on its channel instead:

- `CMD_SET_FLOODLIGHT_MANUAL_SWITCH` (`LightSwitchEvent`), even on cameras without a spotlight (eufyCam 2 Pro);
- `CMD_NOTIFY_PAYLOAD` cmd 55000 (`StorageInfoEvent`, eMMC free/total) once the clip is written.

With the camera alarm enabled (night mode), `CMD_SET_DEVS_TONE_FILE` follows about 4 s later: siren on (`07 00 00 00` = `CameraPir`, then `0a 00 00 00` = 10 s), and an all-zero message when it stops (`CameraSirenEvent`).

`LocalMotionDetector` turns this into two events:

1. **Motion, right away.** The light/IR message's channel picks the camera (cloud device list, `device_channel`), and its time is the event time: `DetectionEvent` with kind `Motion` and source `LightSignal`. Repeats within 30 s (`MotionCooldown`) count as one.
2. **Recording saved, later.** At connect the detector reads the station's recording count per camera (`CMD_DATABASE_QUERY_LATEST_INFO`, read-only). After a motion it re-reads the counts every 5 s (`CheckInterval`) for up to 90 s (`RecordingTimeout`). Any camera whose count went up gets a `RecordingSavedEvent`. Saving lags motion by several seconds, so this event names the camera whose count changed, not the last motion. The newest crop picture's time (`20261101115719_c00.jpg` = 11:57:19, channel 0) is reported only when it is recent, because the station doesn't make a crop for every clip.

The two events mean different things. Motion is the raw PIR trigger: it is fast but can be wind or sun. The camera records only what passes its own check, so a quick walk-by gives motion without a recording. A long stay can give two recordings for one motion. In the walk test on 1 Nov there were 3 walks and 3 motion events. For automations, use motion where speed matters and recording saved where false alarms matter.

No person/vehicle distinction yet: the record list query that would give it (`CMD_DATABASE_QUERY_LOCAL`) returns an empty list on this firmware, and `QUERY_BY_DATE` gets no answer.

These timings are settings under `Eufy:LocalMotion` (`MotionCooldown`, `CheckInterval`, `RecordingTimeout`, `CropMaxAge`). `PollInterval` (e.g. `00:05:00`) also reads the counts without a motion, to catch a missed light message.

`clips [camera]` prints the latest-info and record-list replies and writes them to `data/clips-latest.json` / `data/clips-local.json`.

## Pictures

After a recording with a fresh crop picture, and at connect when the station has a newer crop than the last one downloaded, the bridge fetches it with `CMD_DATABASE_IMAGE` (read-only). The station does not make a crop for every recording.

What HB2 3.4.2.6h sends (checked live on 2 Nov): JSON `{"file","content"}` with URL-safe base64 (`-`/`_`), holding `v2_eufysecurity:<station>:<code>:` and a 640×360 JPEG whose head (quantization tables, frame size, all Huffman tables) is scrambled. The scan from SOS on is plain and uses the standard Huffman tables. `EufyImageV2` rebuilds the head from a standard template and finds the size by Huffman-decoding the scan and counting 8×8 blocks (920 MCUs of 16×16 = 640×360). The real quantization tables are lost, so the tones come out a little flat (Node's decoder has the same limit). The older `eufysecurity:` format (first 256 bytes AES-encrypted with a key from serial + DID) is handled too.

## Battery

Read at connect and every hour from the station's `CMD_CAMERA_INFO` (parameter 1101 per channel; HB2 sends it), otherwise from the cloud device list (`params` 1101 with its update time). The newer value wins. On 2 Nov the station said Backyard 40% while the cloud still had 79% from January, so the local value matters.

## Shutdown and logs

- **Shutdown:** Ctrl+C or SIGTERM (`docker stop`) stops the host, which closes each P2P session with `END` (logged as `Closed the P2P connection … (END)`) within 10 s.
- **Logs:** single-line text by default. `Logging__Console__FormatterName=json` writes one JSON object per line with the fields (`Station`, `Device`, `EventType` …).
- **Capture:** `Eufy:CaptureEvents=true` writes the raw `data/events.jsonl`.

## Capturing reference packets from eufy-security-ws

These steps run on the Pi. They give real HomeBase packets to test the P2P code against.

1. Install tcpdump if it is missing (`sudo apt install tcpdump`) and start capturing UDP to and from the HomeBase:
   `sudo tcpdump -i any -w ~/hb2.pcap udp and host <station ip>`
2. In another shell, add `DEBUG=1` to the eufy-security-ws service's environment in your `compose.yaml`, then recreate it:
   `docker compose up -d <service>`
3. Wait about a minute until the log shows the P2P connection and `set encryption level`. Then change the guard mode once in HA, and back.
4. Stop tcpdump (Ctrl+C) and save the log: `docker logs <container> > ~/ws-debug.log 2>&1`
5. Copy both files to `EufyNet/data/m0/` on Windows (`scp <user>@<pi>:~/hb2.pcap <user>@<pi>:~/ws-debug.log data/m0/`).
6. Remove `DEBUG=1` and recreate the container again.

The log contains tokens and P2P keys. `data/` is git-ignored; keep it that way.
