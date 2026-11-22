# 音のアトリエで作曲・修正する

Codex・Claude Code はどちらも同じ Atelier の MCP に接続する。v0.1.0 の相談画面には「Codex と一緒に考える」と表示される。各クライアントのスキル配置先と接続コマンドは[初期設定と相談例](https://theoriacast.online/guide/#assistants)を参照。

Atelier が選択しているサーバーを使う。GUI の相談は共有された ID を `get_studio_request` で読み、テーマ、歌詞、本人の希望を確認する。`suggest_in_studio` の `reply` は相談者の言語で書き、`changes` には変更する欄だけを入れる。指示書の提案には `prompt_md` を使う。相談者は画面で内容を確認し、適用・作曲予約できる。

直接作曲を頼まれたら `get_nodes` で機器を確認し、`submit_job` を呼ぶ。楽器曲は `generate_bgm`、歌は `generate_vocal`。歌詞欄には実際に歌う行だけを入れ、演奏指示は指示書へ記入する。`get_job` で予約番号を追跡し、完了後は `get_track` で実尺・歌唱照合・残った問題を確認する。

読み修正は `fix_readings` で例として `{"track":"ID","readings":{"市場":"しじょう"}}` を送る。波形の区間差し替え、字幕や歌の時刻、トリム、フェードは `edit_track`、複数曲の連結とクロスフェードは `mix_tracks` を使う。開始・終了は秒。先に原曲と差し替え元の実尺を調べ、修正版の新しい予約番号を返す。

採用状態は `get_review`、依頼された採用・お気に入りの変更は `set_review` で扱う。確定テイクの再現は `get_generation` の結果をそのまま `replay_generation` の `document` に渡す。完了後に WAV の SHA-256 と `replay_verified` を確認する。具体的な引数例は [SKILL.md](../SKILL.md) に記載。

下書きの「2行目だけ短く」という相談は、元の全行を読み、変更後の全文を `changes.lyrics` で返す。たとえば「朝の市場へ／風と歩いていこう」なら、1行目を保ち、2行目を「風と歩こう」にする。歌詞は改行で区切る。完成した録音の言葉を変えるときは元の条件に新しい歌詞を入れた歌ものを予約する。字幕だけの修正は `edit_track` の `cues` に残す行をすべて渡す。同じツールの `timing` は歌と伴奏を一緒に動かす。

ファイルを渡すときは `get_job` が `done` になったら `track` をたどり、`get_track` が返す `audio` を利用者の Atelier の URL に連結して、トラック番号と一緒に伝える。「曲の棚」→「歌詞・作った条件」→「音声を保存」で利用端末に WAV を取得できる。素材は `provenance.materials` にあるものだけ案内する。音源と作曲条件の両方が必要なら WAV と「作曲セットを保存」の ZIP を渡す。[保存画面の例](https://theoriacast.online/guide/#downloads)
