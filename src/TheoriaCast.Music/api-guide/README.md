# GPU サーバーの初期設定と engine.json

GPU で作曲する人向けの手順です。まず GPU 機でエンジン単体の作曲を一度試してください。API を展開したフォルダーで以下の操作を行います。[English](README.en.md)

| ファイル | 誰が作るか | 用途 |
|---|---|---|
| `engine.json` | GPU 機の管理者 | 使うエンジン、モデル、実行ファイル、その出自を指定する入力ファイル |
| `node/node-A.json` または `node/node-B.json` | 同梱の `worker/configure.py` | 入力設定に GPU・ロック・作業場所を加えたワーカーの実行設定。API 起動時に渡す |
| `connection.json` | 起動した API | Atelier が接続先を登録するファイル。利用トークンを含むので、使う人に個別に渡す |

## 1. エンジンに合うひな形を選ぶ

- **BGM を作る**には [ACE-Step 用 JSON](examples/engine-ace-step.json) を使います。例ではノード A が BGM・軽編集・合成を担当します。
- **日本語の歌を作る**には [YuE2 用 JSON](examples/engine-yue2.json) を使います。例ではノード B が歌もの・読み直しを担当します。日本語の歌唱照合用に whisper.cpp の CLI とそのモデル、Python 環境に `pykakasi` も用意してください。

選んだ JSON を API の実行ファイルと同じフォルダーに **engine.json** の名前で保存します。どちらのひな形も Windows・Linux で使えます。例のパスと、すべての `REPLACE_WITH_…` を実際の値に置き換えてください。ファイルは UTF-8 の JSON です。

## 2. 自分の環境の値を書く

| 項目 | 何を書くか |
|---|---|
| `model` / `version` / `license` | 利用するモデルの識別名、導入した版を区別できる文字列、モデル配布元が示したライセンス名。3項目とも必須です。 |
| `model_version.code_repository` / `code_revision` | エンジンのソース URL と、実際にインストールしたコードのコミット ID。Git で導入した場合は `git -C エンジンのフォルダー rev-parse HEAD` で取得できます。半角小文字の16進数40桁を使います。 |
| `model_version.local_patch_sha256` | 独自に当てたパッチの SHA-256。パッチがないときは JSON の `null`。 |
| `components` | 使用した重みごとに1件。各要素に `name`（重みの名前）、`role`（生成器なら `generator`、復号器なら `decoder`）、`source`（配布 URL）、`revision`（取得した重みのコミット ID、同じく小文字40桁）、`license`、`license_evidence`（その版の利用条件を確認できる URL）を記入します。Hugging Face のキャッシュを使う場合は、実際に使用している `snapshots/<コミットID>/` も確認できます。 |
| `types` | この機器が実行できるジョブの配列。`generate_bgm` は楽器曲、`generate_vocal` は歌、`regen_reading_fix` は読み直し、`edit` は編集、`mix` は合成です。 |
| `python` | エンジンと依存パッケージを導入した Python の実行ファイル。仮想環境を有効にして `python -c "import sys; print(sys.executable)"` で確認します。参照アダプターでは Python 3.11 以降、PyTorch、soundfile、各エンジンの依存環境を用意します。 |
| `weights` | GPU 機上のモデルディレクトリ。ACE-Step はチェックポイントの格納先。YuE2 は `YuE2-3B` と `YuE2-Vae` の両方を直下に持つ親フォルダーです。 |
| `ffmpeg` / `generator_module` | FFmpeg 実行ファイルの絶対パスと、生成を担当する Python モジュール名。同梱の参照アダプターを使うときは `reference_generators` のままにします。 |
| `whisper` / `whisper_model` | YuE2 の歌唱照合に使う whisper.cpp の CLI 実行ファイルと、ダウンロード済みの認識モデルファイルの絶対パス。 |

Windows の JSON では `C:/Music/models` のように書けます。Linux の例は `/opt/music/models` です。モデルのフォルダーと FFmpeg・Python の実行ファイルをそれぞれ指定します。ホストで使う秘密トークンは、このひな形には入力しません。

## 3. ノード設定を生成し、API を起動する

API のフォルダーで、まず `nvidia-smi --query-gpu=uuid,name --format=csv,noheader` を実行して対象 GPU を確認します。以下は同一機の API とワーカーを動かす例です。歌用の例では、コマンド中の `A` と `node-A.json` をそれぞれ `B` と `node-B.json` に置き換えます。

**Windows（PowerShell）**

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

- 複数 GPU があるときは configure.py に `--gpu GPU-…` を追加し、一覧の UUID をそのまま指定します。既存の生成処理と GPU を共有するときは、同じロックパスと必要な停止信号（`--pause-file`）を使ってください。
- 1行目が確認するのは JSON の書式です。2行目が出自やジョブ種別を検査し、作成したノード設定のパスを表示します。Python・重み・FFmpeg の実在やエンジンの互換性は、別途、実行環境で確認します。
- 3行目の起動中に新しい `connection.json` ができます。Atelier に取り込み、機器の状態を確認し、短い BGM または数行の歌を予約して試聴します。標準設定では空き VRAM が 8,192 MiB 以上あり、ほかの作業が空くまで待ちます。終了は API のターミナルで Ctrl+C です。
- 別 PC やクラウドから利用する場合は、Atelier が到達できる HTTPS の URL を API の `--public-url` に指定します。待受アドレス、リバースプロキシ、追加ワーカーのトークン設定は[インストール手順](../README.md)を参照してください。

## 4. 起動できない、設定を変更したい

| 症状 | 確認すること |
|---|---|
| `code_revision` または `components` のエラー | コードと各重みのコミットを取り違えていないか、40桁か、配布元の URL とライセンスの根拠を入力したか確認します。短い SHA、タグ名、ひな形の文字列は使えません。 |
| `FileExistsError` | configure.py は既存のノード設定を上書きしません。変更時は旧設定を保管し、新しい `--root` で生成して API を停止・再起動します。`--node-config` を新しいファイルに向け、既存の `--Music:Data`、GPU ロック、共用の停止ファイルを引き継ぎます。`engine.json` だけを書き換えても実行中の設定は変わりません。 |
| 予約後にモジュールまたはファイルが見つからない | `python` がエンジン用の仮想環境を指すか確認します。YuE2 では2つの重みフォルダー、whisper-cli とモデルファイルも確認します。FFmpeg は指定した実行ファイルで `-version` が出ることを確かめてください。 |
| 接続はできるが予約が進まない | ノードの `types`、空き VRAM、他の GPU 処理、停止ファイルの有無を確認します。 |

**独自エンジンの場合**は `render_command` に実行ファイルと固定引数を配列で指定できます（例 `["/opt/music/bin/my-renderer"]`）。`model`、`version`、`license`、`types` は共通の必須項目です。ワーカーは配列の末尾に「ノード設定パス」「ジョブ作業フォルダー」の順で追加します。独自レンダラーは `job.json` と `inputs/` を読み、`deliver/` に `master.wav`、出自を記録した `master.wav.json`、`preview.mp3` を書きます。歌には照合結果も必要です。[参照レンダラー](../worker/render.py)
