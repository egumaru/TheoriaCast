# Set up a GPU server with engine.json

Run these steps in the extracted API folder on the GPU computer. First, generate a short sample with your installed music engine to check its drivers, environment and weights. [日本語](README.md)

| File | Created by | Purpose |
|---|---|---|
| `engine.json` | You | Describes the installed engine, files, supported jobs and model provenance |
| `node/node-A.json` or `node/node-B.json` | The supplied `worker/configure.py` | Adds the GPU, shared lock and working directory. Pass this file to the API at startup. |
| `connection.json` | The running API | Lets Atelier register this server. Transfer it privately because it includes a client token. |

## 1. Pick a template

- For **instrumental music**, start with the [ACE-Step JSON](examples/engine-ace-step.json). The example assigns instrumental generation, edits and mixes to node A.
- For **Japanese songs**, start with the [YuE2 JSON](examples/engine-yue2.json). The example assigns vocal generation and pronunciation fixes to node B. For the lyric check, install the whisper.cpp CLI, a Japanese-capable Whisper model and the Python package `pykakasi`.

Save the selected template as **engine.json** next to the API executable. Both templates work on Windows and Linux. Replace the example paths and every `REPLACE_WITH_…` value with information from your installation. Save the file as UTF-8 JSON.

## 2. Fill in your installation details

| Field | What to enter |
|---|---|
| `model` / `version` / `license` | The installed model identifier, a version string distinguishing this installation and the license named by the model publisher. All three strings are required. |
| `model_version.code_repository` / `code_revision` | The engine source URL and the exact code commit you installed. For a Git checkout, run `git -C PATH_TO_ENGINE rev-parse HEAD`. Supply 40 lowercase hexadecimal characters. |
| `model_version.local_patch_sha256` | SHA-256 of a local patch, or JSON `null` for unmodified code. |
| `components` | One entry per set of weights. Provide `name`, `role` (e.g. `generator` or `decoder`), `source` (upstream URL), `revision` (the installed weights’ 40-character lowercase commit), `license` and `license_evidence` (a link to the license applicable to that revision). If you use the Hugging Face cache, inspect the actual `snapshots/<commit>/` directory used by your engine. |
| `types` | Jobs this device can run. `generate_bgm` generates instrumentals, `generate_vocal` generates songs, `regen_reading_fix` remakes a song with new pronunciations, `edit` edits tracks and `mix` combines them. |
| `python` | The interpreter with your engine and dependencies installed. In the activated environment run `python -c "import sys; print(sys.executable)"`. The reference renderer uses Python 3.11+, PyTorch, soundfile and the engine’s dependencies. |
| `weights` | Absolute path to the ACE-Step checkpoint directory, or the YuE2 parent directory containing both `YuE2-3B` and `YuE2-Vae`. |
| `ffmpeg` / `generator_module` | Absolute path to the FFmpeg executable and the Python module that generates audio. Keep `reference_generators` for the included adapters. |
| `whisper` / `whisper_model` | For YuE2, absolute paths to the whisper.cpp CLI executable and its downloaded speech recognition model file. |

Forward slashes work in Windows JSON paths, e.g. `C:/Music/models`. A typical Linux path is `/opt/music/models`. The templates contain no access tokens.

## 3. Generate the node profile and start the API

In the API folder, run `nvidia-smi --query-gpu=uuid,name --format=csv,noheader` to identify the GPU. These commands run the API and its worker on the same machine. For the vocal example, replace `A` with `B` and pass `node-B.json` at startup.

**Windows (PowerShell)**

```powershell
py -m json.tool engine.json --no-ensure-ascii
py worker/configure.py A --engine-manifest ./engine.json --root ./node --api http://127.0.0.1:39400 --lock C:/Music/shared/gpu.lock
.\TheoriaCast.Music.Api.exe --urls http://127.0.0.1:39400 --node-config ./node/node-A.json --Music:Data ./library --connection-file ./connection.json
```

**Linux**

```sh
python3 -m json.tool engine.json --no-ensure-ascii
python3 worker/configure.py A --engine-manifest ./engine.json --root ./node --api http://127.0.0.1:39400 --lock /srv/music/shared/gpu.lock
./TheoriaCast.Music.Api --urls http://127.0.0.1:39400 --node-config ./node/node-A.json --Music:Data ./library --connection-file ./connection.json
```

- If more than one GPU is present, add `--gpu GPU-…` to configure.py with the full UUID you selected. When sharing a device with an existing scheduler, use the same lock path and any shared pause signals (`--pause-file`).
- The first command checks JSON syntax. The second validates the provenance and job types and prints the path to the new profile. Test Python, weights and FFmpeg separately in the engine’s environment.
- The third command starts the API and writes `connection.json`. Import it into Atelier, check the device status, then queue and audition a short instrumental or a few lines of a song. With the default gate the worker waits for at least 8,192 MiB free VRAM and for other work to finish. Stop the foreground API with Ctrl+C.
- For another computer or a cloud GPU, pass an HTTPS address reachable from Atelier as `--public-url`. The [installation manual](../README.en.md) covers listeners, reverse proxies and additional worker tokens.

## 4. Troubleshooting and changing settings

| Symptom | What to check |
|---|---|
| Invalid `code_revision` or `components` | Use the exact commits for the engine code and each set of weights, 40 lowercase hexadecimal characters each. Include the source and license evidence. Branches, short hashes and template placeholders fail validation. |
| `FileExistsError` | configure.py creates a new file. Preserve the old profile, generate a new one using a fresh `--root`, stop the API and restart it with the new `--node-config`. Keep the same `--Music:Data`, GPU lock and any shared pause file. Editing the input manifest alone does not reload the running worker. |
| A job reports a missing module or file | Check the interpreter from the engine environment. For YuE2, check both weights directories, the Whisper executable and its model. Run the configured FFmpeg executable with `-version`. |
| The connection is online but a job waits | Check the node’s `types`, free VRAM, other GPU tasks and pause files. |

**Custom engines** can specify `render_command` as an array containing the executable and fixed arguments, e.g. `["/opt/music/bin/my-renderer"]`. The common fields `model`, `version`, `license` and `types` are still required. The worker appends the node profile path and job directory in that order. The renderer reads `job.json` and `inputs/`, then writes `master.wav`, its provenance sidecar `master.wav.json` and `preview.mp3` into `deliver/`. Vocal jobs also need their lyric audit. [Reference renderer](../worker/render.py)
