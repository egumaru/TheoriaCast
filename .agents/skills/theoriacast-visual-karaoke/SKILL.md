---
name: theoriacast-visual-karaoke
description: Generate TheoriaCast karaoke bar videos, scrolling staff videos, or transparent lyric overlays from existing Music guide notes, timed lyric cues, and karaoke WAV using the Visual MCP server.
---

# Generate karaoke videos

Use the Music shelf that owns the track. From the completed guide extract or edit job, read `recipe.notes` with Music MCP `get_export`. From the corresponding take, read `provenance.cues` with `get_track` or use its exported SRT. Keep the original `start`, `duration`, `end`, `midi`, and display `text`. Start and end values are seconds on the selected WAV's clock. Use a corresponding karaoke or guide-mix WAV export and its track ID when available. Resolve timing differences against the Music data before submitting.

## Visual MCP

1. Split the WAV into chunks of up to 384 KiB. Call `visual_stage_karaoke_audio` with `{"chunk":{"bytes":"<base64>"}}` for each chunk. Save the returned SHA-256 digests in file order and compute the SHA-256 of the whole file. For subsequent jobs on the same Visual server, give the whole WAV digest alone.
2. Call `visual_submit_karaoke` with the chosen mode and timing data.

   ```json
   {
     "request": {
       "mode": "bars",
       "input": {
         "title": "Track title",
         "trackId": "music-track-id",
         "audio": {"sha256": "<whole WAV SHA-256>", "chunks": ["<chunk SHA-256>"]},
         "notes": [{"start": 1.5, "duration": 0.5, "midi": 64}],
         "lyrics": [{"start": 1.5, "end": 3.0, "text": "歌詞の行"}],
         "bpm": 120,
         "beatsPerBar": 4
       },
       "style": {"width": 1920, "height": 1080, "fps": 30, "head": 0.34, "scroll": 0.22}
     }
   }
   ```

   Use `bars` for the piano roll, `score` for the scrolling staff, or `lyrics` for the transparent MOV overlay. The default lyric styles are respectively `preview`, `underline`, and `follow`. Set `style.lyrics` to choose a different lyric treatment for any mode. A lyric-only overlay can omit notes. For a score, omit BPM when no authoritative tempo is available. Other style values include `lines`, `leadMs`, `holdMs`, colors, `fontSize`, and `lyricsTop`. The recipe includes effective defaults.
3. Poll `visual_get_karaoke_job` with the returned ID. On completion, call `visual_read_karaoke_video` with the job ID, a byte `offset` starting at 0, and a `count` of up to 393216. Decode and append the returned bytes until reaching `totalBytes`. Compare the saved file's SHA-256 with `videoSha256`. Use MP4 for bars and score, and MOV for the alpha overlay.
4. Use `visual_get_karaoke_recipe` for provenance. For timing checks, select zero-based frames around lyric and note boundaries and call `visual_get_karaoke_frame`. Compare the plan with those frames decoded from the saved video. For a repeat, use `visual_replay_karaoke` and compare completed hashes. If the hashes differ, decode and compare the sampled frames and audio track, and report the method.

The Visual API host needs a configured FFmpeg with the `ass` filter and an installed font that supports the lyric language. Use `Visual:KaraokeFFmpeg`, `Visual:KaraokeFont`, and `Visual:KaraokeFontFamily`. The same inputs can be selected under カラオケ動画 in Atelier. The generation output is separate from Music tracks and publication actions.
