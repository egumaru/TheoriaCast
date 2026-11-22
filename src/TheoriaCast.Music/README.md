# TheoriaCast.Music ― 音のアトリエ

[English](README.en.md) · [公式サイト](https://theoriacast.online/) · [GitHub Releases](https://github.com/egumaru/TheoriaCast/releases)

TheoriaCast の配信に使う BGM と歌を作る付属ツールです。GPU 機で作曲エンジンと API を起動し、利用端末の Atelier に接続ファイルを読み込みます。GPU 機は手元の PC、LAN 内の別の PC、クラウドの GPU サーバーのいずれでも利用できます。API と Atelier を同一の PC で使うこともできます。

## ダウンロードとインストール

1. GitHub Releases の Music リリースから、GPU 機には **TheoriaCast.Music.Api**、ブラウザを使う端末には **TheoriaCast.Music.Atelier** を選びます。各々 Windows x64 用 ZIP と Linux x64 用 tar.gz があります。両方を同じ PC に置くときも別フォルダーに展開します。.NET ランタイムは同梱しています。
2. Windows は ZIP を展開します。Linux は `tar -xzf <アーカイブ名> -C <作成済みの展開先>` を使います。各アーカイブの SHA-256 はリリース付属の `SHA256SUMS` と照合できます。
3. GPU 機に NVIDIA ドライバーを用意し、`nvidia-smi` で GPU が表示されることを確認します。利用する生成エンジンとモデルをその配布元の手順で別途インストールし、単体で生成できることを確認します。モデルの重み、第三者の作曲エンジン、その利用許諾は別途必要です。
4. API の展開先に `engine.json` を作ります。次は ACE-Step 用の参照アダプターを選ぶ例です。パスは絶対パスで、モデル名・改訂番号・ライセンスを実際のインストールに合わせて設定します。Windows は例えば `C:/Music/venv/Scripts/python.exe` と `C:/Music/ffmpeg/bin/ffmpeg.exe` を指定します。

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

参照アダプターには Python 3.11 以降、soundfile、FFmpeg と、エンジンの依存パッケージが必要です。歌用の YuE2 は `types` に `generate_vocal` と `regen_reading_fix` を指定し、重みフォルダーに `YuE2-3B` と `YuE2-Vae` を配置します。`whisper` と `whisper_model` に whisper.cpp の CLI とモデルの絶対パスを設定し、Python 環境に pykakasi を用意します。この参照アダプターの歌唱照合は日本語用です。独自エンジンでは `render_command` に実行ファイルと引数の配列を指定できます。ワーカーが末尾にノード設定パスと作業フォルダーを渡します。作業フォルダーの `job.json` に ID と spec、`inputs/` に素材、完了時に `deliver/` に master.wav、master.wav.json、preview.mp3 を置く契約です。

### GPU 機で API を起動する

次は同一 PC で試す例です。上は Windows、下は Linux。既存ランナーがいる場合は、それと同じ GPU ロックパスを指定します。複数 GPU の機器では `--gpu GPU-…` を追加します。

```powershell
py worker/configure.py A --root ./node --api http://127.0.0.1:39400 --lock C:/Music/shared/gpu.lock --engine-manifest engine.json
.\TheoriaCast.Music.Api.exe --urls http://127.0.0.1:39400 --Music:Data ./library --node-config ./node/node-A.json --connection-file ./connection.json
```

```sh
python3 worker/configure.py A --root ./node --api http://127.0.0.1:39400 --lock /srv/music/shared/gpu.lock --engine-manifest engine.json
./TheoriaCast.Music.Api --urls http://127.0.0.1:39400 --Music:Data ./library --node-config ./node/node-A.json --connection-file ./connection.json
```

起動時に API が **connection.json** を出力します。利用端末に安全に渡してください。利用者用トークンが入っています。API のデータディレクトリにある `server-access.json` は管理用です。

**LAN・クラウドからの接続**　Atelier から到達できる URL を `--public-url` に指定します。クラウドでは HTTPS のリバースプロキシで、例えば `https://music.example.org/` から API の `http://127.0.0.1:39400` へ転送し、API 引数に `--public-url https://music.example.org/` を追加します。証明書、名前解決、受信規則は設置環境で用意します。TLS 終端後の非ループバック接続では信頼するプロキシ IP を `--Distributed:TrustedProxies:0` に設定します。信頼する LAN で HTTP を選ぶ場合は API に LAN の待受アドレスと公開アドレスを指定し、API と Atelier の両方に `--Distributed:AllowPlainHttp true` を渡します。

### 利用端末で Atelier を起動する

```powershell
.\TheoriaCast.Music.Atelier.exe --port 39395
```

```sh
./TheoriaCast.Music.Atelier --port 39395
```

1. ブラウザで `http://localhost:39395/` を開き、最初の画面の「接続ファイルを読み込む」で **GPU 機が作成した connection.json** を選びます。
2. サーバー名の横が「接続中」になることを確認します。別サーバーのファイルを追加し、「ここに切り替える」で選べます。右上の言語欄で日本語と English を選べます。
3. 「曲を作る」で種類、テーマ、雰囲気、長さを入力して予約します。「予約と履歴」に順番と機器が表示され、「曲の棚」で再生、採用、修正ができます。終了は各端末で Ctrl+C。再起動時は同じデータフォルダーを指定します。

## 作曲と修正

- 歌詞は歌う行だけを書き、演奏の指示は「曲の指示書」に書きます。読み置換は全行に適用されます。参照歌唱アダプターは文字起こしと照合を最大3テイクまで行い、合否と残った箇所を返します。
- 指示書（prompt.md）、歌詞（lyrics.txt）、ABC 楽譜（score.abc）を個別に読み込み・保存できます。composition.json は編集用の条件、ZIP は素材一式です。「AI と指示書を作る」は現在の下書きを Codex に共有する相談を作成します。依頼文を Codex に渡し、届いた変更案を画面で確認します。
- 生成済みテイクから generation.json を保存して読み込み、「元テイクを再現」で改めて予約できます。確定した引数・シード・実行環境とマスター WAV の SHA-256 を照合します。モデル、実行環境、音声が一致しない場合は失敗として理由を返します。生成済み WAV は保管してください。
- 「曲を直す」で波形をドラッグし、範囲を試聴して切り取りや差し替えを予約します。一定テンポの曲は BPM、拍子、最初の小節位置を指定して小節に合わせられます。字幕時刻、歌と伴奏の位置、クロスフェード、個別音量も調整できます。結果は新しいトラックです。

## 別の GPU ワーカーと運用

API に同居させるワーカーは `--node-config` で明示します。別機のワーカーでは稼働済みの API のデータフォルダーを指定して `TheoriaCast.Music.Api worker-token ./library ./worker.token` を実行し、生成したファイルをその機器だけに渡します。ノード作成時に `--token-file <絶対パス>` を追加して `TheoriaCast.Music.Api worker <node.json> run` で起動します。Windows の実行名には .exe が付きます。対応種別はノードの初回ハートビートまたは API の `--Music:Capabilities:B:0 generate_vocal` 等で宣言します。未対応の予約は受付時に理由が返ります。

ワーカーは空き VRAM 8,192 MiB 以上、共有ロック、GPU の使用状況、停止信号を確認します。既存の作業を優先するにはランナーと同じ `lock`、`pause_files`、必要に応じて `busy_command` を設定します。優先作業を検知すると実行中テイクを停止してキューに戻します。`worker <node.json> pause|resume|status|stop` で操作できます。API は SQLite と `生成音源/TheoriaCast/` に予約・音声・出自を保存します。Atelier の localhost は同じ端末のユーザーとプロセスを信頼する構成です。

[画面で見る操作ガイド](https://theoriacast.online/guide/)

スクリーンショット付きの操作ガイドは、起動した Atelier の「使い方」からオフラインでも開けます。

## API と Codex

Atelier の MCP 接続先は `http://localhost:39395/mcp`。Codex では `codex mcp add theoriacast-music --url http://localhost:39395/mcp` で登録します。配布物の `skills/theoriacast-music` を作業プロジェクトの `.agents/skills/` に配置すると、作曲・編集の手順を呼び出せます。直接 API を使う場合は接続記述子の URL と利用トークンを使います。

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

MCP ツールは `list_studio_requests` · `get_studio_request` · `suggest_in_studio` · `submit_job` · `get_job` · `list_jobs` · `get_nodes` · `list_tracks` · `get_track` · `get_generation` · `replay_generation` · `fix_readings` · `mix_tracks` · `edit_track` · `get_review` · `set_review` です。本ツールのライセンスは Apache-2.0 です。配布物の LICENSE と THIRD-PARTY 文書に適用条件を収録しています。


Windows 上の既存 EgumaruMusic ウォッチャーと同居する例では、ノード設定の `requires_legacy_interlock` を true にし、`busy_command` に `["powershell.exe","-NoProfile","-NonInteractive","-File","C:/Music/api/worker/legacy-idle.ps1"]` を指定します。この確認はウォッチャーが稼働中なら待機します。既存側で共有ロックを使わない場合の同時起動を防ぐためです。
