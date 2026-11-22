---
name: theoriacast-music
description: Compose music and revise generated tracks through the TheoriaCast.Music MCP server. Use when working with Sound Atelier, an open composition consultation, BGM or vocal jobs, pronunciation corrections, waveform edits, mixes, take reproduction, or track review.
---

# Compose and revise music

Use the MCP server connected to the user's Atelier. Codex and Claude Code share the tools and can answer the same GUI consultation. Calls use the server selected in that Atelier. In the tool picker names may have the configured MCP server name as a prefix. Read the Japanese guide when working in Japanese. [日本語](references/ja.md)

## Start from a draft

When the user is working in the GUI, open the ID they shared with `get_studio_request({"id":"…"})`. If they asked about an open request without an ID, use `list_studio_requests` and match the question. Use the question, language, theme and draft as the brief. Return advice with `suggest_in_studio`. Keep suggested changes to fields the request concerns. The person can preview and apply the result in their browser.

```json
{"id":"REQUEST_ID","reply":"A sparse piano opening leaves room for the spoken introduction.","changes":{"prompt_md":"Open with solo piano. Add soft strings after the opening phrase. End with a short, clean cadence."}}
```

For a request to generate music directly, call `get_nodes`, then `submit_job`. Job types available depend on configured engines. Supply a theme, mood and target seconds. Put performance directions in `prompt_md`; the lyrics field contains only the lines to sing. For the Japanese vocal adapter use kana reading substitutions across the whole lyric sheet. Optional `seed`, `bpm` and vocal `score_abc` can be included in the spec.

```json
{"spec":{"type":"generate_bgm","theme":"Opening a family photo album","style":"gentle piano and acoustic guitar","seconds":30,"priority":"interactive"}}
```

For a lyric rewrite, read the consultation's current `draft.lyrics`. Return the complete revised lyrics in `changes.lyrics`, preserving the other lines, line breaks and language. For example, changing only the second line of “To the morning market / Let us walk along with the wind” gives:

```json
{"id":"REQUEST_ID","reply":"The shorter second line fits in one breath.","changes":{"lyrics":"To the morning market\nWalk with the wind"}}
```

For a finished recording, new sung words require a new vocal generation. Use the original composition settings and the revised lyric sheet. A pronunciation fix keeps the written lyrics and applies the supplied readings to matching text across the sheet. For subtitle-only edits, read the existing cues and send the complete desired cue list, including unchanged lines. The optional timing map moves the mixed voice and backing together.

## Follow and revise a take

Use `get_job({"id":"JOB_ID"})` for state and position. Queued jobs wait until a matching device has spare resources. Reuse the returned ID on subsequent checks. For a completed job follow its `track` with `get_track({"id":"TRACK_ID"})`. Read the measured duration, provenance, cues and the vocal audit with its attempts and unresolved issues. Report an interrupted or failed job with its recorded reason. Playback URLs are relative to the selected Atelier. Use `list_jobs` or `list_tracks` to find existing work.

| Goal | Tool | Example arguments |
|---|---|---|
| Correct a Japanese reading | `fix_readings` | `{"track":"VOCAL_ID","readings":{"市場":"しじょう"}}` |
| Trim and fade | `edit_track` | `{"track":"ID","start":2,"end":12,"fade_in":0.2,"fade_out":1}` |
| Replace a passage | `edit_track` | `{"track":"ID","start":4,"end":8,"replacement":"DONOR","replacement_start":1}` |
| Move a vocal and its accompaniment | `edit_track` | `{"track":"ID","timing":[{"from":4,"to":4.5}]}` |
| Change captions | `edit_track` | `{"track":"ID","cues":[{"start":0,"end":3.5,"text":"Morning comes"}]}` |
| Join with a crossfade | `mix_tracks` | `{"clips":[{"track":"A","gain":1},{"track":"B","gain":0.8}],"mode":"join","crossfade":1}` |
| Check selection | `get_review` | `{"track":"ID"}` |
| Mark as a favorite | `set_review` | `{"track":"ID","review":{"favorite":true}}` |

Check source and donor durations first. Times are seconds. Map bar selections using the known tempo, beats per bar and first downbeat. Each render creates a new track. Return the new job ID and verify the resulting track. Set adoption only when requested; MCP decisions record Codex as reviewer.

## Hand over finished files

After `get_job` reports `done`, follow its `track` with `get_track`. Return the track ID and resolve its `audio` path against the listener's local Atelier (default `http://localhost:39395`). In Library, expand **Lyrics and settings** and choose **Save audio** for a WAV on the user's computer. For text or a recipe, offer the files actually listed in `provenance.materials`. **Save composition bundle** downloads the composition settings and available materials. Download the WAV separately when handing over a recording and its project. A completed edit or mix has its own track ID. [Illustrated download instructions](https://theoriacast.online/guide/en/#downloads)

For exact reproduction, call `get_generation({"track":"ID"})` on a take with a frozen recipe. Pass the returned document intact to `replay_generation({"document":…})`. Verify `result.replay_verified` and `result.sha256` after completion. Changing an instruction, seed or score creates a variation. An older track, edit or mix may have no frozen generator recipe. Never describe a queued replay as a verified match.
