# EufyNet

EufyNet connects a **Eufy HomeBase 2** and its cameras to **Home Assistant** and **Node-RED** over MQTT, and keeps the
Eufy cloud out of the loop as much as possible. It talks to the HomeBase directly on your LAN and runs as one small
Docker container, for example on a Raspberry Pi next to Home Assistant.

It is a .NET 10 port of the parts of [eufy-security-client](https://github.com/bropat/eufy-security-client) /
eufy-security-ws that log in and talk to the HomeBase, extended with motion detection that needs no cloud push.

## What you get

In Home Assistant (found automatically through MQTT discovery, no YAML):

| Device | Entities |
| --- | --- |
| HomeBase | **Guard mode** (select; changing it arms or disarms for real), **Active mode** (what the station applies right now, e.g. Home while Schedule is selected), **Connected** |
| Each camera | **Motion** (on within a second of the camera's PIR trigger, off after 30 s), **Last motion**, **Last recording**, **Motions (8 h)**, **Recordings (8 h)**, **Battery**, **Last picture** (image), **Picture taken** |

For Node-RED and other automations, every event is also published as JSON on MQTT (motion, recording saved, new picture,
siren, alarm, guard mode change …), and commands can be sent the same way. See [MQTT topics](#mqtt-topics).

## How it works

```
             once at start / hourly                    LAN, UDP (P2P)
 Eufy cloud ───────────────────────┐        ┌───────────────────────────── HomeBase 2 ── cameras
  login, device list,              ▼        ▼
  battery fallback              ┌──────────────┐   MQTT    ┌──────────────┐
                                │   EufyNet    │──────────▶│  Mosquitto   │──▶ Home Assistant
                                │  (Docker)    │◀──────────│              │──▶ Node-RED
                                └──────────────┘ commands  └──────────────┘
```

1. **Cloud, only to start.** EufyNet logs in to your Eufy account (with the emailed code the first time; afterwards it is a
   trusted device and the session is reused) and reads the list of stations and cameras with the keys needed to reach the
   HomeBase. The cloud is also the fallback for battery levels.
2. **Local P2P to the HomeBase.** It opens Eufy's P2P protocol directly to the HomeBase's LAN address, keeps it alive, and
   reconnects if it drops. Guard mode changes, pictures and status go over this connection.
3. **Motion without the cloud.** A HomeBase 2 does not push detections over P2P (the app gets them from the cloud). It
   does tell the bridge the moment a camera's PIR triggers, on that camera's channel. EufyNet reports that as **motion**
   right away. It then watches the HomeBase's recording counter per camera and reports **recording saved** when the
   clip is written. Motion is fast but includes walk-bys the camera did not find worth recording; a recording means the
   camera's own check passed. Use whichever fits the automation.
4. **Pictures.** After a recording, EufyNet downloads the HomeBase's preview picture of it (when the station made one)
   and publishes it to Home Assistant.
5. **MQTT.** Everything is published to your broker with Home Assistant discovery, and commands (guard mode, picture …)
   come back the same way.

Protocol details and what was observed on real hardware: [docs/protocol-notes.md](docs/protocol-notes.md).

## Requirements

- A **HomeBase 2** with battery cameras. Tested with firmware 3.4.2.6h and eufyCam 2 Pro; other HomeBase models may
  work but are untested.
- A **Raspberry Pi** (or any arm64/x64 Linux box) with **Docker** and the compose plugin, on the same LAN as the HomeBase.
- An **MQTT broker**, e.g. the Mosquitto add-on/container you use for Home Assistant, with the MQTT integration set up
  in Home Assistant.
- Your Eufy account's email, password and country. Consider a separate Eufy account that you invite to the HomeBase as
  an admin from the Eufy app: the bridge stays logged in, and some clients log each other out when they share one account.

## Deploy on a Raspberry Pi

The steps assume your compose file is `~/DockerCompose/compose.yaml`; adjust the paths to yours.

**1. Create the data folder** next to your compose file. The image is ready-made on GitHub Container Registry
(`ghcr.io/myso225/eufynet`, arm64 and amd64), built from this repository by GitHub Actions, so there is nothing to build.

```bash
mkdir -p ~/DockerCompose/eufynet/data
```

**2. Find your HomeBase's serial and LAN IP.** The serial starts with `T8010`; it is in the Eufy app's HomeBase settings,
and the bridge logs it at start (`Station T8010… : model T8010 …`), so you can also start without the IP line and add it
afterwards. The LAN IP is in your router's client list; give it a fixed DHCP lease.

**3. Add the service** from [docker/compose.eufynet.yaml](docker/compose.eufynet.yaml) under `services:` in your
compose file and fill in the `<…>` values:

```yaml
  eufynet:
    image: ghcr.io/myso225/eufynet:latest
    container_name: eufynet
    network_mode: host
    restart: unless-stopped
    user: "1000:1000"            # your uid:gid from `id`, owner of ./eufynet/data
    environment:
      - TZ=<Region/City>
      - Eufy__Username=<eufy email>
      - "Eufy__Password=<eufy password>"
      - Eufy__Country=<country>  # two letters, as in the Eufy app, e.g. DE
      - Eufy__StationIpAddresses__<station serial>=<station ip>
      - Mqtt__Host=127.0.0.1     # the broker; 127.0.0.1 when it runs on the same machine
      - Mqtt__Username=<mqtt user>
      - "Mqtt__Password=<mqtt password>"
      - Mqtt__GuardModes__Away=Away
      - Mqtt__GuardModes__Home=Home
      - Mqtt__GuardModes__Schedule=Schedule
      - Mqtt__GuardModes__Custom1=Night
      - Mqtt__GuardModes__Disarmed=Disarmed
    volumes:
      - ./eufynet/data:/app/data
    stdin_open: true
    tty: true
    stop_grace_period: 15s
```

- In passwords write `$` as `$$`, and keep the quotes.
- `user:` matters: the image runs without root, so the container needs your uid to write `data/`.
- `Mqtt__GuardModes__*` sets which modes the Home Assistant select offers and their names. The names in the Eufy app
  for custom modes are not available to the bridge, so label them yourself (see [Guard modes](#guard-modes)).
- Prefer to keep passwords out of `compose.yaml`? Put `EUFY_PASSWORD=…` in the `.env` file next to it and write
  `- Eufy__Password=${EUFY_PASSWORD}`.

**4. Start it** and watch the log:

```bash
cd ~/DockerCompose && docker compose up -d eufynet && docker logs -f eufynet
```

Ctrl+C leaves the log; the container keeps running.

**5. First login.** On the very first start Eufy emails you a verification code and the log says so. Enter it in the
container's console:

```bash
docker attach eufynet
```

Type the code and press Enter (an empty line asks for a new code). Leave the console with **Ctrl+P, Ctrl+Q**; Ctrl+C
would stop the bridge. Alternatively publish `{"name":"verify_code","args":{"code":"123456"}}` to `eufynet/command`.
The session is then saved in `data/` and later starts need no code.

**6. Check.** A healthy start looks like this:

```
Connected to the MQTT broker 127.0.0.1:1883, topics under eufynet/
Logged in to the Eufy cloud
P2P connected to T8010… at 192.168.1.x:…
Front Door battery 54% (station)
T8010… ready: guard mode Schedule, encryption Level1. Type help for commands
Event CameraImage Front Door: picture … (26 KB, taken …)
```

The warning `Cannot use cloud cipher …, Using Level 1` is expected; eufy-security-client does the same. In Home
Assistant, the HomeBase and each camera appear under Settings → Devices & services → MQTT.

### Updating

```bash
cd ~/DockerCompose && docker compose pull eufynet && docker compose up -d eufynet
```

`latest` follows the main branch. To stay on a release instead, use a version tag such as `ghcr.io/myso225/eufynet:1.0`.

### Building the image yourself

Clone the repository (on the Pi or anywhere with Docker) and replace the `image:` line with `build: <path to the clone>`,
then `docker compose up -d --build eufynet`. The `Dockerfile` builds from source inside Docker, so no .NET SDK is needed.
For another platform: `docker buildx build --platform linux/arm64 -t eufynet .`

## Guard modes

The HomeBase 2 knows Away, Home, Schedule, Custom 1–3, Geofencing and Disarmed. List the ones you want in Home Assistant
with your own names:

```yaml
      - Mqtt__GuardModes__Custom1=Night
```

Without any `Mqtt__GuardModes__*` entries, all modes are offered under their standard names. To find which custom mode
is which, switch modes in the Eufy app and watch `guard_mode_code` on `eufynet/<station serial>/state`, or type `status`
in the console. A mode the station reports that is not in your list still shows, under its standard name.

Choosing a mode in Home Assistant, Node-RED or the console **really arms or disarms the HomeBase**.

## Console

`docker attach eufynet`, then (Ctrl+P, Ctrl+Q to leave):

| Command | |
| --- | --- |
| `status` | connection, guard mode, and per camera last motion/recording, counters and battery |
| `picture [camera]` | download the newest preview picture now |
| `mode <name> [station]` | set the guard mode (away, home, schedule, custom1 …) |
| `info [station]` | the HomeBase's raw camera info (read-only) |
| `clips [camera]` | the HomeBase's raw recording info (read-only) |
| `help`, `quit` | `quit` stops the bridge |

## MQTT topics

`eufynet` is the default prefix (`Mqtt__BaseTopic`). Payloads are JSON unless noted.

| Topic | Retained | |
| --- | --- | --- |
| `eufynet/status` | yes | `online` / `offline` |
| `eufynet/login` | yes | login state; `needs_code: true` means a code is awaited |
| `eufynet/<station>/state` | yes | `guard_mode` (your label), `guard_mode_code`, `alarm_mode`, `connected` |
| `eufynet/<camera>/state` | yes | `last_motion`, `last_recording`, `motions`, `recordings`, `battery`, `picture_taken` |
| `eufynet/<camera>/image` | yes | the last picture, JPEG bytes |
| `eufynet/<camera>/motion` | no | `ON` per motion |
| `eufynet/<camera>/event`, `eufynet/<station>/event` | no | `{"type": …, "device", "name", "time", …}`; types `motion`, `recording`, `picture_saved`, `picture`, `siren`, `alarm`, `alarm_mode`, `guard_mode`, `light`, `storage` … |
| `eufynet/<station>/guard_mode/set` | in | a label (`Night`) or mode name (`Custom1`) |
| `eufynet/command` | in | `{"id":"1","name":"picture","args":{"camera":"Front Door"}}`; names as in the console: `status`, `picture`, `set_guard_mode` (arg `mode`), `info`, `clips`, `verify_code` (arg `code`) |
| `eufynet/result` | no | `{"id","name","success","message","data"}` for each command |

## Configuration

Set these as environment variables in the compose file (`__` separates levels). Defaults are in
[appsettings.json](src/EufyNet.App/appsettings.json).

| Variable | Default | |
| --- | --- | --- |
| `Eufy__Username`, `Eufy__Password` | – | Eufy account |
| `Eufy__Country` | `US` | account country, two letters |
| `Eufy__StationIpAddresses__<serial>` | – | HomeBase LAN IP; without it the HomeBase is searched by broadcast |
| `Eufy__ActivityWindow` | `08:00:00` | span of the motion/recording counters |
| `Eufy__BatteryInterval` | `01:00:00` | how often batteries are read |
| `Eufy__DownloadPictures` | `true` | fetch preview pictures |
| `Eufy__LocalMotion__MotionCooldown` | `00:00:30` | PIR triggers closer than this are one motion |
| `Mqtt__Host`, `Mqtt__Port` | –, `1883` | broker; without a host MQTT is off |
| `Mqtt__Username`, `Mqtt__Password` | – | broker login |
| `Mqtt__BaseTopic` | `eufynet` | topic prefix |
| `Mqtt__DiscoveryPrefix` | `homeassistant` | empty turns discovery off |
| `Mqtt__GuardModes__<mode>` | all modes | select options and labels |
| `Mqtt__MotionOffDelay` | `00:00:30` | when the motion sensor turns off |
| `Logging__Console__FormatterName` | `simple` | `json` for one JSON object per log line |

`data/` holds the cloud session (a login token: keep it private), the counters (`activity.json`, so a restart does
not reset them) and the last picture per camera.

## Limitations

- Motion has no person/vehicle/pet distinction: the HomeBase 2 does not report it locally.
- The HomeBase does not make a preview picture for every recording, so the picture can be older than the last
  recording; **Picture taken** says when it is from. Pictures come out a little flat in tone, because the station
  scrambles the JPEG's quantization tables and standard ones are used instead.
- No live video or clip download yet.

## Development

```bash
dotnet user-secrets set "Eufy:Username" "you@example.com" --project src/EufyNet.App
dotnet user-secrets set "Eufy:Password" "your-password" --project src/EufyNet.App
dotnet user-secrets set "Eufy:Country" "DE" --project src/EufyNet.App
dotnet run --project src/EufyNet.App
```

```bash
dotnet test
```

Code layout and protocol notes: [docs/protocol-notes.md](docs/protocol-notes.md).

## License

MIT, see [LICENSE](LICENSE). The protocol logic is ported from
[eufy-security-client](https://github.com/bropat/eufy-security-client) (MIT, © bropat); see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Not affiliated with Eufy or Anker.
