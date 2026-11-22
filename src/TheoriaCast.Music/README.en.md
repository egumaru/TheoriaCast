# TheoriaCast.Music — Sound Atelier

[日本語](README.md) · [Official site](https://theoriacast.online/en/) · [GitHub Releases](https://github.com/egumaru/TheoriaCast/releases)

Queue background music and songs for TheoriaCast, audition takes and edit them in a browser. Install the engine and API on a **local or cloud GPU machine**. Run Atelier on the computer where you use the browser. Both programs can also run on the same computer.

## Install the two applications

[GPU setup guide, engine.json fields and templates](https://theoriacast.online/en/install/api/)

1. On the Music GitHub Release, choose **TheoriaCast.Music.Api** for the GPU server and **TheoriaCast.Music.Atelier** for the browser computer. Each is available separately for Windows x64 (ZIP) and Linux x64 (tar.gz). Extract each archive into its own folder. The .NET runtime is included. You can verify each archive against the release’s SHA256SUMS.
2. Install the NVIDIA driver on the GPU host and check that `nvidia-smi` lists your GPU. Install and test your chosen generation engine and model following the supplier’s instructions. Supply the engine, weights and their applicable licenses separately.
3. In the API folder create `engine.json`. This example selects the supplied integration adapter for an existing ACE-Step installation. Replace the identifiers, paths and license with those of your installation. Use absolute paths, e.g. `C:/Music/venv/Scripts/python.exe` and `C:/Music/ffmpeg/bin/ffmpeg.exe` on Windows.

```json
{
  "model": "YOUR_INSTALLED_MODEL_ID",
  "version": "YOUR_MODEL_AND_CODE_REVISIONS",
  "license": "YOUR_MODEL_LICENSE",
  "model_version": {
    "code_repository": "URL_OF_THE_INSTALLED_ENGINE",
    "code_revision": "FULL_40_CHARACTER_ENGINE_COMMIT",
    "local_patch_sha256": null
  },
  "components": [{
    "name": "YOUR_INSTALLED_MODEL_ID",
    "role": "generator",
    "source": "URL_OF_THE_WEIGHT_REPOSITORY",
    "revision": "FULL_40_CHARACTER_WEIGHT_COMMIT",
    "license": "YOUR_MODEL_LICENSE",
    "license_evidence": "URL_OF_THE_MODEL_LICENSE_AT_THIS_REVISION"
  }],
  "types": ["generate_bgm", "edit", "mix"],
  "python": "/opt/music/venv/bin/python",
  "weights": "/opt/music/weights",
  "ffmpeg": "/usr/bin/ffmpeg",
  "generator_module": "reference_generators"
}
```

The reference adapter needs Python 3.11+, soundfile, FFmpeg and the engine's Python dependencies. For YuE2 vocals, declare `generate_vocal` and `regen_reading_fix`, place YuE2-3B and YuE2-Vae under the weights directory, configure `whisper` and `whisper_model` with the paths to whisper.cpp and its model, and install pykakasi. The reference vocal audit supports Japanese lyrics. For another engine, supply `render_command` as an argument array. The worker appends the profile and work-directory paths. Read the ID and spec from job.json and source materials from inputs/. Write master.wav, master.wav.json and preview.mp3 to deliver/.

### Start the API on the GPU machine

Run these commands from the extracted API directory. The example uses loopback for a single computer. On a shared GPU, use the existing runner's lock path. Add `--gpu GPU-…` when multiple GPUs are present.

```powershell
py worker/configure.py A --root ./node --api http://127.0.0.1:39400 --lock C:/Music/shared/gpu.lock --engine-manifest engine.json
.\TheoriaCast.Music.Api.exe --urls http://127.0.0.1:39400 --Music:Data ./library --node-config ./node/node-A.json --connection-file ./connection.json
```

```sh
python3 worker/configure.py A --root ./node --api http://127.0.0.1:39400 --lock /srv/music/shared/gpu.lock --engine-manifest engine.json
./TheoriaCast.Music.Api --urls http://127.0.0.1:39400 --Music:Data ./library --node-config ./node/node-A.json --connection-file ./connection.json
```

The API writes **connection.json** on startup. Transfer this file privately to the Atelier computer. It contains a client token. Keep the separate server-access.json in the API data directory on the host.

**LAN or cloud setup.** Set `--public-url` to an address reachable from Atelier. On a cloud GPU, configure an HTTPS reverse proxy and certificate, forwarding e.g. `https://music.example.org/` to `http://127.0.0.1:39400`. Add `--public-url https://music.example.org/` to the API command. Configure DNS and inbound traffic on your infrastructure. For a non-loopback upstream behind TLS termination, set the proxy IP with `--Distributed:TrustedProxies:0`. For plain HTTP on a trusted LAN, bind to a LAN address, advertise the corresponding LAN URL and pass `--Distributed:AllowPlainHttp true` to both applications.

### Start Atelier and connect

```powershell
.\TheoriaCast.Music.Atelier.exe --port 39395
```

```sh
./TheoriaCast.Music.Atelier --port 39395
```

1. Open `http://localhost:39395/`. On first launch choose **Import a connection file** and select the **connection.json created by the GPU server**.
2. Wait for **Online** next to the server name. Import another descriptor to add a server, then use **Switch to this server**. The language menu switches between English and Japanese.
3. Choose **Compose**, enter a theme, mood and length, and queue a track. **Queue and history** shows the device and queue position. Listen, adopt or edit the result in **Library**. Stop either foreground application with Ctrl+C. Reuse the same data directories on restart.

## Compose, edit and reproduce

- Put only singable lines in the lyrics field. Put performance directions in the prompt. Japanese reading substitutions apply to the entire sheet. The reference adapter transcribes and checks up to three vocal takes, reporting the outcome and outstanding differences.
- Import or save prompt.md, lyrics.txt and score.abc individually. Save editable settings in composition.json or the materials together in a ZIP. **Draft a prompt with AI** prepares an Atelier consultation. Paste its request into Codex or Claude Code and preview the suggested prompt in the browser.
- Download generation.json from a generated take, import it and choose **Reproduce original take** to queue a verified replay. The renderer checks frozen arguments, seed and runtime. The server checks the master WAV hash. A mismatch fails the job. Keep the original WAV as the authoritative recording.
- In **Edit music**, drag on the waveform to select a passage, audition or loop it, then queue a trim or replacement. For constant-tempo tracks, enter the tempo, beats per bar and first downbeat to snap to bars. Adjust caption times, vocal timing, fades or gain, or join two tracks with a crossfade. Edits generate new tracks.

## Additional workers and storage

Use `--node-config` to enable a worker on the API host. For another GPU machine, export a scoped worker token from the running server's existing data directory with `TheoriaCast.Music.Api worker-token ./library ./worker.token`. Transfer it privately, generate the remote profile with `--token-file <absolute path>`, then run `TheoriaCast.Music.Api worker <node.json> run`. Windows executables have an .exe suffix. Declare capabilities on first worker heartbeat or with API options such as `--Music:Capabilities:B:0 generate_vocal`. Jobs without a configured engine return an error at submission.

Workers check the shared lock, GPU activity, pause files and at least 8,192 MiB of available VRAM. Use the same lock and pause signals as any existing generation or verification runner. Configure a local `busy_command` when needed. Priority work interrupts a take and returns it to the queue. Worker controls are `worker <node.json> pause|resume|status|stop`. The API stores jobs in SQLite and recordings with their provenance under `生成音源/TheoriaCast/`. Atelier trusts other processes on its local computer.

[Illustrated Atelier guide](https://theoriacast.online/guide/en/) · [Revise lyrics and captions](https://theoriacast.online/guide/en/#lyrics) · [Download finished files](https://theoriacast.online/guide/en/#downloads)

The illustrated user guide is also available offline from **User guide** in Atelier.

## API, Codex and Claude Code

Register the local Atelier with `codex mcp add theoriacast-music --url http://localhost:39395/mcp`. Copy the archive's `skills/theoriacast-music` directory into your project’s `.agents/skills/` for the composition and editing skill. For Claude Code, copy the same skill folder into the project’s `.claude/skills/` and run `claude mcp add --transport http theoriacast-music http://localhost:39395/mcp` from that project. Follow the [consultation walkthrough](https://theoriacast.online/guide/en/#assistants) in either client. Direct API clients use the address and bearer token from their connection descriptor.

| Method | Path |
|---|---|
| GET / POST | `/jobs` |
| GET | `/jobs/revision`, `/jobs/{id}`, `/nodes`, `/works` |
| POST | `/jobs/{id}/cancel`, `/tracks/{id}/adopt` |
| GET | `/tracks`, `/tracks/{id}`, `/tracks/{id}/audio`, `/tracks/{id}/captions`, `/tracks/{id}/waveform?start=0&end=5` |
| GET | `/tracks/{id}/materials/{name}`, `/tracks/{id}/composition.zip` |
| POST | `/composition/import`, `/composition/pack` |
| PUT | `/works/{name}` |
| POST | `/studio/requests`, `/studio/requests/{id}/decision` |
| GET | `/studio/requests/{id}`, `/heartbeat` |
| MCP | `/mcp` |
| GET (worker) | `/worker/demand/{node}`, `/worker/jobs/{id}`, `/worker/tracks/{id}`, `/worker/tracks/{id}/audio` |
| POST (worker) | `/worker/poll`, `/worker/{id}/progress`, `/worker/{id}/assign`, `/worker/{id}/finish` |

MCP tools: `list_studio_requests` · `get_studio_request` · `suggest_in_studio` · `submit_job` · `get_job` · `list_jobs` · `get_nodes` · `list_tracks` · `get_track` · `get_generation` · `replay_generation` · `fix_readings` · `mix_tracks` · `edit_track` · `get_review` · `set_review`. The Music code is licensed under Apache-2.0. See LICENSE and the third-party notices in each archive.


For a Windows host with the existing EgumaruMusic scheduler, set `requires_legacy_interlock` to true and set `busy_command` to `["powershell.exe","-NoProfile","-NonInteractive","-File","C:/Music/api/worker/legacy-idle.ps1"]` using your actual path. The worker waits while the existing scheduler runs, including when that scheduler does not use the shared GPU lock.
