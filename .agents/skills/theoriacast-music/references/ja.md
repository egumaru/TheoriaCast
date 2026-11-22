# 音のアトリエで作曲・修正する

Atelier が選択しているサーバーを使う。GUI の相談は共有された ID を `get_studio_request` で読み、テーマ、歌詞、本人の希望を確認する。`suggest_in_studio` の `reply` は相談者の言語で書き、`changes` には変更する欄だけを入れる。指示書の提案には `prompt_md` を使う。相談者は画面で内容を確認し、適用・作曲予約できる。

直接作曲を頼まれたら `get_nodes` で機器を確認し、`submit_job` を呼ぶ。楽器曲は `generate_bgm`、歌は `generate_vocal`。歌詞欄には実際に歌う行だけを入れ、演奏指示は指示書へ記入する。`get_job` で予約番号を追跡し、完了後は `get_track` で実尺・歌唱照合・残った問題を確認する。

読み修正は `fix_readings` で例として `{"track":"ID","readings":{"市場":"しじょう"}}` を送る。波形の区間差し替え、字幕や歌の時刻、トリム、フェードは `edit_track`、複数曲の連結とクロスフェードは `mix_tracks` を使う。開始・終了は秒。先に原曲と差し替え元の実尺を調べ、修正版の新しい予約番号を返す。

採用状態は `get_review`、依頼された採用・お気に入りの変更は `set_review` で扱う。確定テイクの再現は `get_generation` の結果をそのまま `replay_generation` の `document` に渡す。完了後に WAV の SHA-256 と `replay_verified` を確認する。具体的な引数例は [SKILL.md](../SKILL.md) に記載。
