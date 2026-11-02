# EufyNet

.NET 10 replacement for the parts of `eufy-security-ws` that log in to the Eufy cloud and talk to the HomeBase.
The sections below follow the milestones it was built in (M0–M6).

Protocol logic is ported from [eufy-security-client](https://github.com/bropat/eufy-security-client)
(MIT, © bropat and contributors; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)).

## Layout

| Project | What |
|---|---|
| `src/EufyNet.Cloud` | Cloud login (ECDH + AES, 2FA, captcha, trusted device), session persistence, station/device lists |
| `src/EufyNet.P2P` | UDP session, commands, encryption, events, local motion detection |
| `src/EufyNet.Core` | `EufyDriver` (login → stations → P2P, state, commands) and the transport contract (`IBridgeTransport`, `TransportBridge`) |
| `src/EufyNet.Transport.Console` | Transport: events and state as log lines, commands and login answers from stdin |
| `src/EufyNet.Transport.Mqtt` | Transport: MQTT with Home Assistant discovery, events for Node-RED, commands in |
| `src/EufyNet.App` | Generic Host wiring |
| `docker/` | Dockerfile, compose service and env example for the Pi 5 |
| `tests/EufyNet.Tests` | Crypto checked against Node-generated vectors; login flow against a fake Eufy server; P2P against a fake station |

`nuget.config` limits restore to nuget.org, so machine-wide private feeds are not needed.

## M1 — log in and list stations

```bash
dotnet user-secrets set "Eufy:Username" "you@example.com" --project src/EufyNet.App
dotnet user-secrets set "Eufy:Password" "your-password" --project src/EufyNet.App
dotnet run --project src/EufyNet.App
```

- `Country` defaults to `US` (`src/EufyNet.App/appsettings.json`). It must match the account's country, e.g. `dotnet user-secrets set "Eufy:Country" "DE" --project src/EufyNet.App`.
- On the first run Eufy emails a code. Type it at the prompt; the app then registers itself as trusted device `eufynet`.
- The session is saved to `data/cloud-session.json` (relative to the current directory). It holds the auth token, so keep it private. Later runs restore it without 2FA. `--force-login` ignores it.
- `data/stations.json` and `data/devices.json` hold the raw decrypted lists (`p2p_did`, `app_conn`, `member` …). They are the input for the P2P milestones.
- Environment variables work too: `Eufy__Username`, `Eufy__Password`, `Eufy__Country`, `Eufy__DataDir`.

## M2 — P2P connection to the HomeBase

After listing, the app keeps a P2P session open to every station until Ctrl+C (`--list-only` skips this).

- `Eufy:P2PConnectionSetup`: `OnlyLocal` (default, LAN only) or `Quickest` (also asks Eufy's cloud lookup servers, using the station's DSK key).
- `Eufy:StationIpAddresses`: station LAN IPs by serial. The cloud's `ip_addr` is the public IP, so set this. Without an entry the lookup is broadcast.
- Flow: `LOCAL_LOOKUP` → `CHECK_CAM` (port ±3) → `CAM_ID` → `PING` every 5 s. Every incoming DATA packet is ACKed. After 50 s without a `PONG` the session ends and reconnects (backoff 5 → 60 s).
- Every 5 minutes it logs a status line. `Logging__LogLevel__EufyNet.P2P=Debug` shows each lookup step and DATA packet.

The code is in `src/EufyNet.P2P`: `P2PPackets` (builders/parsers, checked against Node vectors), `P2PSession` (one connection), `P2PStationConnector` (reconnect loop).

## M3 — commands: encryption and guard mode

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

## M4 — events from the HomeBase

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

Every CONTROL and unsolicited DATA message (except pings) is also appended to `data/events.jsonl` (time, command, channel, payload as text and hex). That file is the raw material for new parsers.

### Motion on a HomeBase 2 without the cloud

The capture showed that HB2 (3.4.2.6h) sends no detection push over P2P. When a camera records, two messages arrive on its channel instead:

- `CMD_SET_FLOODLIGHT_MANUAL_SWITCH` (`LightSwitchEvent`), even on eufyCam 2C without a spotlight;
- `CMD_NOTIFY_PAYLOAD` cmd 55000 (`StorageInfoEvent`, eMMC free/total) once the clip is written.

With the camera alarm enabled (night mode), `CMD_SET_DEVS_TONE_FILE` follows about 4 s later: siren on (`07 00 00 00` = `CameraPir`, then `0a 00 00 00` = 10 s), and an all-zero message when it stops (`CameraSirenEvent`).

`LocalMotionDetector` turns this into two events:

1. **Motion, right away.** The light/IR message's channel picks the camera (cloud device list, `device_channel`), and its time is the event time: `DetectionEvent` with kind `Motion` and source `LightSignal`. Repeats within 30 s (`MotionCooldown`) count as one.
2. **Recording saved, later.** At connect the detector reads the station's recording count per camera (`CMD_DATABASE_QUERY_LATEST_INFO`, read-only). After a motion it re-reads the counts every 5 s (`CheckInterval`) for up to 90 s (`RecordingTimeout`). Any camera whose count went up gets a `RecordingSavedEvent`. Saving lags motion by several seconds, so this event names the camera whose count changed, not the last motion. The newest crop picture's time (`20261101115719_c00.jpg` = 11:57:19, channel 0) is reported only when it is recent, because the station doesn't make a crop for every clip.

The two events mean different things. Motion is the raw PIR trigger: it is fast but can be wind or sun. The camera records only what passes its own check, so a quick walk-by gives motion without a recording. A long stay can give two recordings for one motion. In the walk test on 1 Nov there were 3 walks and 3 motion events. For automations, use motion where speed matters and recording saved where false alarms matter.

No person/vehicle distinction yet: the record list query that would give it (`CMD_DATABASE_QUERY_LOCAL`) returns an empty list on this firmware, and `QUERY_BY_DATE` gets no answer.

These timings are settings under `Eufy:LocalMotion` (`MotionCooldown`, `CheckInterval`, `RecordingTimeout`, `CropMaxAge`). `PollInterval` (e.g. `00:05:00`) also reads the counts without a motion, to catch a missed light message.

`clips [camera]` prints the latest-info and record-list replies and writes them to `data/clips-latest.json` / `data/clips-local.json`.

## M0 — capture golden vectors from the running Node bridge

These steps run on the Pi. They give real HomeBase packets to test the P2P code against (M2/M3).

1. Install tcpdump if it is missing (`sudo apt install tcpdump`) and start capturing UDP to and from the HomeBase:
   `sudo tcpdump -i any -w ~/hb2.pcap udp and host <station ip>`
2. In another shell, add `DEBUG=1` to the eufy-security-ws service's environment in your `compose.yaml`, then recreate it:
   `docker compose up -d <service>`
3. Wait about a minute until the log shows the P2P connection and `set encryption level`. Then change the guard mode once in HA, and back.
4. Stop tcpdump (Ctrl+C) and save the log: `docker logs <container> > ~/ws-debug.log 2>&1`
5. Copy both files to `EufyNet/data/m0/` on Windows (`scp <user>@<pi>:~/hb2.pcap <user>@<pi>:~/ws-debug.log data/m0/`).
6. Remove `DEBUG=1` and recreate the container again.

The log contains tokens and P2P keys. `data/` is git-ignored; keep it that way.

## M5 — transport seam, shutdown, Docker

`EufyDriver` does the work and knows no transport. It raises `LoginChanged`, `StateChanged` (per station: connected, guard/alarm mode, encryption, last motion/recording per camera) and `EventRaised` (`BridgeEvent` = station event + camera). It executes `BridgeCommand`s: `status`, `set_guard_mode`, `info`, `clips`, and the login answers `verify_code`, `resend_code` and `captcha`. `TransportBridge` connects it to an `IBridgeTransport`. MQTT (M6) is a second transport implementing that interface; each transport gets its own `TransportBridge`.

Console transport: the commands are as before (`status | info [station] | clips [camera] | picture [camera] | mode <name> [station] | quit`). While the login waits, a bare line is the emailed code or the captcha answer, and an empty line asks for a new code.

- **Shutdown:** Ctrl+C or SIGTERM (`docker stop`) stops the host, which closes each P2P session with `END` (logged as `Closed the P2P connection … (END)`) within 10 s.
- **Logs:** single-line text by default. `Logging__Console__FormatterName=json` writes one JSON object per line with the fields (`Station`, `Device`, `EventType` …).
- **Capture:** `Eufy:CaptureEvents=true` writes the raw `data/events.jsonl` (off by default since M4).

### Docker on the Pi 5

```bash
dotnet publish src/EufyNet.App -c Release -r linux-arm64 --self-contained false -o docker/app
```

`docker/` then holds everything for the image: the Dockerfile only copies `app/` onto `mcr.microsoft.com/dotnet/runtime:10.0`, so it builds on the Pi or with `docker buildx build --platform linux/arm64` without emulation. Copy `docker/` to an `eufynet` folder next to your `compose.yaml` along with `data/cloud-session.json` from the PC, so the Pi needs no new 2FA login.

Then add `compose.eufynet.yaml`'s service to the compose file and fill in the `<…>` values (Eufy login and country, the station's LAN IP, MQTT). Compose treats `$` as a variable, so write a `$` in the password as `$$`. If `compose.yaml` is backed up or committed, put the password in the `.env` next to it instead (`EUFY_PASSWORD=…`) and use `Eufy__Password=${EUFY_PASSWORD}`.

It runs on a Raspberry Pi 5 (arm64) next to Home Assistant's Mosquitto.

## M6 — MQTT and Home Assistant

Set `Mqtt:Host` (env `Mqtt__Host`) and the bridge also connects to an MQTT broker, next to the console. Tested against an in-process MQTTnet broker; not yet against Home Assistant. Home Assistant finds the devices through MQTT discovery; no YAML is needed. The broker can be down at start or go away: the bridge reconnects every 5 s and then publishes everything again.

| Setting | Default | |
| --- | --- | --- |
| `Mqtt:Host`, `Port`, `Username`, `Password` | –, 1883 | broker |
| `Mqtt:BaseTopic` | `eufynet` | prefix of all topics below |
| `Mqtt:DiscoveryPrefix` | `homeassistant` | empty = no discovery |
| `Mqtt:GuardModes:<mode>` | all HomeBase 2 modes | labels for the select, e.g. `Mqtt__GuardModes__Custom1=Night`; only listed modes are offered |
| `Mqtt:MotionOffDelay` | 30 s | the motion sensor's `off_delay` |
| `Eufy:ActivityWindow` | 8 h | span of the motion/recording counters |
| `Eufy:BatteryInterval` | 1 h | battery refresh |
| `Eufy:DownloadPictures` | true | crop picture after a recording and at connect |

**Home Assistant entities**

- HomeBase: *Guard mode* (select; **changing it sets the real alarm**), *Active mode* (what the station applies, e.g. Home while Schedule is selected), *Connected*.
- Each camera: *Motion* (on at the light/IR signal, off after 30 s), *Last motion*, *Last recording*, *Motions (8 h)*, *Recordings (8 h)*, *Battery*, *Last picture* (image entity) and *Picture taken*.

The counters and times are kept in `data/activity.json`, so a restart does not reset them.

**Topics** (all JSON unless noted)

| Topic | Retained | |
| --- | --- | --- |
| `eufynet/status` | yes | `online` / `offline` (last will) |
| `eufynet/login` | yes | login state; `needs_code: true` = send `verify_code` |
| `eufynet/<station>/state` | yes | `guard_mode` (label), `guard_mode_code`, `alarm_mode`, `connected` … |
| `eufynet/<camera>/state` | yes | `last_motion`, `last_recording`, `motions`, `recordings`, `battery`, `picture_taken` … |
| `eufynet/<camera>/image` | yes | JPEG bytes |
| `eufynet/<camera>/motion` | no | `ON` per motion |
| `eufynet/<camera>/event`, `eufynet/<station>/event` | no | for Node-RED: `{"type":"motion"\|"recording"\|"picture"\|"siren"\|"light"\|"storage"\|"alarm_mode"\|"guard_mode"\|"alarm"…, "device", "name", "time", …}` |
| `eufynet/<station>/guard_mode/set` | in | a label (`Night`) or mode name (`Custom1`) |
| `eufynet/command` | in | `{"id":"1","name":"picture","station":null,"args":{"camera":"Front Door"}}`; the same commands as the console (`status`, `info`, `clips`, `picture`, `set_guard_mode`, `verify_code` …) |
| `eufynet/result` | no | `{"id","name","success","message","data"}` |

**Pictures.** After a recording whose crop picture is fresh, and at connect when the station has a crop newer than the last one downloaded, the bridge downloads it over P2P (`CMD_DATABASE_IMAGE`, read-only). It is published to the image topic and saved as `data/<camera>.jpg`. The station does not make a crop for every recording, so the picture can be older than the last recording; *Picture taken* says when it is from. `picture [camera]` in the console fetches the newest crop on demand.

What HB2 3.4.2.6h sends (checked live on 2 Nov): JSON `{"file","content"}` with URL-safe base64 (`-`/`_`), holding `v2_eufysecurity:<station>:<code>:` and a 640×360 JPEG whose head (quantization tables, frame size, all Huffman tables) is scrambled. The scan from SOS on is plain and uses the standard Huffman tables. `EufyImageV2` rebuilds the head from a standard template and finds the size by Huffman-decoding the scan and counting 8×8 blocks (920 MCUs of 16×16 = 640×360). The real quantization tables are lost, so the tones come out a little flat (Node's decoder has the same limit). The older `eufysecurity:` format (first 256 bytes AES-encrypted with a key from serial + DID) is handled too.

**Battery.** Read at connect and every hour from the station's `CMD_CAMERA_INFO` (parameter 1101 per channel; HB2 sends it), otherwise from the cloud device list (`params` 1101 with its update time). The newer value wins. On 2 Nov the station said Backyard 40% while the cloud still had 79% from January, so the local value matters.
