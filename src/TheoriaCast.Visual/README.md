# 素材のアトリエ（Visual Atelier）

PNG・JPEG の取り込み、一括イラスト化、サイズ変換、背景編集、素材セットの確認・承認を行います。画像は選択した API サーバーの素材棚に保存されます。参照にはサーバー識別子、素材 ID、SHA-256 を使います。

## 起動

Windows x64 と .NET 10 SDK を使います。ローカルでは次の2つを別々に起動します。

```powershell
dotnet run --project src/TheoriaCast.Visual.Api
dotnet run --project src/TheoriaCast.Visual.Atelier
```

Visual の4プロジェクトはルートの `TheoriaCast.Visual.slnx` でまとめてビルドできます。

[素材のアトリエ](http://localhost:39416) を開き、`%LOCALAPPDATA%\TheoriaCast\Visual\connection.json` を接続ファイルとして読み込みます。ファイルには Bearer トークンが含まれます。既定では API は `127.0.0.1:39415`、Atelier は `localhost:39416` で待ち受けます。素材棚の保存先は `Visual:Data` で指定できます。複数のサーバーを同時に起動するときは保存先とポートを個別に指定します。

素材棚で画像またはフォルダを取り込み、選択した画像を一括編集します。ジョブの完了後に「結果を素材セットで確認」を開き、各素材の判定を済ませて版を承認します。未接続の参照もセットに残り、「確認不可」と表示されます。承認履歴から制作条件を確認するときは各素材の「レシピ」を開きます。

## 編集と計算資源

| 編集 | 処理 |
| --- | --- |
| サイズ変換・トリミング | CPU の OpenCV で中央を切り出すか、構図を維持して余白を追加。上下左右に個別の安全余白を指定 |
| 写真のイラスト化 | CPU は画像 Provider 0.3.0 の OpenCV NPR、CUDA ノードは FLUX.2 [klein] 4B。タッチと強さを指定。CPU で漫画を選ぶと鉛筆画で処理 |
| 背景除去・無地・手持ち画像 | CPU の BiRefNet_lite で透過マスクを推定。元画像の輪郭に合わせる境界補正、マスクの収縮、縁に残る背景色の除去を選択可能 |
| 生成背景 | FLUX.2 [klein] 4B で元画像を参照した背景編集を行い、BiRefNet_lite で抜き出した前景と合成 |
| 素材の色合わせ | CPU の OpenCV で、基準画像の Lab 分布と明るさに合わせる。強さは0〜1 |
| 素材の検品 | 画素検査と、登録済みの Cosmos 3 Edge によるタグ付けを非同期で実行。判定と承認は手動 |

横 MV、縦ショート、記事用などのプリセットはサイズ変換で選べます。余白は単色、四辺の平均色、ぼかしから選びます。素材セットの「タッチと強さを比較」では代表画像1〜2枚を比較し、コンタクトシートで選んだレシピをセット全体に適用できます。カット順、表示時間、メモ、歌詞行番号は承認前に設定し、承認後は画像とともに ZIP で出力できます。

背景編集では `--Visual:MattingModel <birefnet-lite-512.onnx>` を API に渡します。FLUX を利用するサーバーでは `--Visual:FluxModels <固定モデルのディレクトリ>` を渡します。モデルは Provider が実行時に SHA-256 を照合します。バージョン更新は、モデルを別ディレクトリに置いた上で運用者が行います。アプリからモデルを取得・更新する通信はありません。 [BiRefNet_lite の準備](https://github.com/AIKernel-NET/AIKernel.Providers/blob/61fe465/src/Image/AIKernel.Providers.Image/docs/IMAGE.ja.md) · [FLUX モデルの準備](https://github.com/AIKernel-NET/AIKernel.Providers/blob/9fa32e5/src/Image/AIKernel.Providers.Flux/FLUX.md#register-the-local-model-files)

Atelier の「背景のエッジ補正」では、元画像上のガイド半径 `maskRefineRadius` を0〜32px、収縮幅 `maskErodePx` を0〜2pxで指定し、`decontaminateEdge` で縁に残る背景色を除去できます。設定は新しいレシピに記録されます。既定の `0 / 0 / false` では従来どおりの処理です。

Cosmos 3 Edge を利用する場合は `Visual:CosmosModel` に取り込み済みのモデルのディレクトリ、`Visual:CosmosRevision` に40桁のモデルリビジョン、`Visual:CosmosManifestSha256` に承認済みの `import-manifest.json` の SHA-256 を設定します。モデル登録時は新規取り込みが自動検品の対象になります。`Visual:AutoInspect=off` で手動登録だけに切り替え、`Visual:AutoInspect=pixels` で新規取り込み時の画素検査を有効にできます。素材棚ではファイル名と検品タグで検索できます。

Cosmos によるタグ付けは NVIDIA Cosmos 3 Edge (4B) を使用します。検品結果に NVIDIA のモデル名、固定版、SHA-256、OpenMDW-1.1 の帰属情報を記録します。 [NVIDIA Cosmos 3 Edge](https://huggingface.co/nvidia/Cosmos3-Edge) · [OpenMDW-1.1](https://openmdw.ai/license/1-1/)

画素検査では、透過画像の前景に囲まれた穴や、前景から離れて画像端に残った不透明領域も要確認候補として報告します。処理レシピのある素材は、検品画面で元画像と結果を見比べられます。要確認フラグが付いた素材も、採否は確認者が決めます。検品の判定方式としきい値はレシピに記録されます。

CUDA ノードでは互換 NVIDIA ドライバーと CUDA 13.2 のランタイム、cuBLAS が必要です。`Visual:CudaDevice` は CUDA のデバイス番号で、既定は `0` です。CPU の動作を確認するときは `--Visual:EnableGpu=false` を指定できます。 [CUDA Provider の要件](https://github.com/AIKernel-NET/AIKernel.Providers/blob/dev/src/Image/AIKernel.Providers.Flux.Cuda/FLUX-CUDA.md)

別サーバーを参照する場合は、API セッションに次の設定を追加します。

```json
{
  "Visual": {
    "Peers": [
      { "Connection": "C:/private/local-gpu-connection.json", "Hardware": "gpu" },
      { "Connection": "C:/private/cloud-gpu-connection.json", "Hardware": "cloud-gpu" },
      { "Connection": "C:/private/asset-server-connection.json" }
    ]
  }
}
```

各接続ファイルは先方の Visual API が出力したものを使います。ネットワーク上の接続は既定で HTTPS です。自動振り分けは利用可能なローカル CUDA、登録した GPU ノード、CPU の順です。クラウド GPU は明示して選択したジョブに使われ、そのノードへ元画像を送信します。所要時間は概算です。ノードごとの 256 px の概算秒数は `Visual:GpuSecondsAt256`、CPU FLUX は `Visual:CpuFluxSecondsAt256` で調整できます。

新しく登録する生成背景ジョブを別の GPU ノードに振り分ける場合は、ノード側にも画像 Provider 0.3.0 に対応した Visual API を用意します。

## API と MCP

再実行には、レシピに記録された処理条件と互換性のある画像 Provider を使います。0.2.0 で作ったレシピは、追加のエッジ補正を無効にして 0.3.0 で再実行できます。0.1.4 のレシピを再実行する場合は、0.1.4 を使用する元の Visual API 環境を使います。

API は `/api/v1/visual/`、Streamable HTTP MCP は接続ファイルの `mcp_url` を使います。いずれも Bearer 認証で同じ素材・ジョブ・レシピを操作します。

| 操作 | API | MCP |
| --- | --- | --- |
| 取り込み | `POST /assets/chunks`, `POST /assets/import` | `visual_stage_image_chunk`, `visual_import_asset` |
| 画像本体の取得 | `GET /asset-refs/{server}/{id}/{hash}/image` | `visual_read_image` |
| 見積もりと一括登録 | `POST /jobs/estimate`, `POST /jobs` | `visual_estimate_batch`, `visual_submit_batch` |
| 比較ジョブ | `POST /jobs/preview-sweep` | `visual_preview_sweep` |
| 進捗と処理結果 | `GET /jobs/{id}` | `visual_get_job` |
| レシピ確認と再実行 | `GET /recipes/{id}`, `POST /recipes/replay` | `visual_get_recipe`, `visual_replay_recipe` |
| 素材棚検索 | `POST /assets/search` | `visual_search_assets` |
| 素材セットの確認・承認 | `GET /sets/{id}`, `POST /sets/{id}/review`, `POST /sets/{id}/approve` | `visual_get_set`, `visual_review_set_item`, `visual_approve_set` |
| 承認済みセットの出力 | `GET /sets/{id}/manifest`, `GET /sets/{id}/export` | `visual_get_set_manifest` |

大きな画像は 384 KiB 以下のチャンクに分け、返された SHA-256 を元の順に並べて登録します。取り込み時に元ファイル全体の SHA-256 をサーバーで確認します。上限は画像あたり 16 MiB、4,194,304 画素、バッチあたり 200 件です。

JPEG は新規レシピの処理時に EXIF の向きを適用し、元のバイト列を素材棚に保持します。既存レシピの再実行では保存当時の向きを使います。画像の取得を含む MCP の手順は [SKILL.md](../../.agents/skills/theoriacast-visual/SKILL.md) を参照してください。

画像処理は項目単位、カラオケ動画はジョブ単位で試行回数を保存します。再起動後は待機中の項目を先に処理します。中断からの再開を含めて上限は3試行です。

`library.json` は更新のたびに全体を書き出します。回帰試験では素材61点・27ジョブで約125 KiBでした。数十素材・数十ジョブ程度の案件を当面の利用目安とし、長期運用では状態ファイルのサイズと更新時間を確認してください。状態の分割、索引、保持期間の設定は次段の課題です。

レシピはサーバーの JSON と処理済み PNG に保存されます。`{ "recipeId": "…" }` で再実行し、`"change": { "parameter": "strength", "value": 0.4 }` を追加すると1項目だけ変えた新レシピを親レシピに紐づけて登録します。同じモデルと条件を固定し、レシピで再実行できます。画素は処理環境によって異なることがあります。別サーバーの素材レシピは参照先につながる間は閲覧できます。再実行は素材の保管先サーバーから行います。

サイズ変換の差分では `targetResolution` の値に `{ "width": 1080, "height": 1920 }` を渡します。`setId` を指定すると、比較ジョブの採用候補やサイズ変換の差分レシピをセット全体に適用できます。検品は `operation: "inspect"` で登録し、結果はジョブの `items[].inspection` に保存されます。

## カラオケ動画

API の `Visual:KaraokeFFmpeg` と `Visual:KaraokeFont` に、ASS フィルターを備えた FFmpeg と歌詞を表示できるフォントの絶対パスを指定します。フォント名の既定値は `Noto Sans JP` です。別のフォントを使用する場合は `Visual:KaraokeFontFamily` も指定します。

Atelier の「カラオケ動画」で Music のガイド譜 JSON、歌詞の SRT または JSON、同じ曲とテイクのカラオケまたはガイド入り WAV を選びます。カラオケ風バーと流れる楽譜は MP4、歌詞だけの透過オーバーレイは MOV に書き出します。既定の解像度は 1920×1080 です。レシピには入力の音符・歌詞タイミング、WAV の SHA-256、描画設定、FFmpeg とフォントの SHA-256 が残ります。

MCP での入力例、書き出し、フレーム照合、レシピによる再生成は [SKILL.md](../../.agents/skills/theoriacast-visual-karaoke/SKILL.md) を参照してください。

API と MCP では歌詞を `input.lyrics` のキュー配列または `input.srt` の UTF-8 テキストで渡します。Atelier の SRT ファイルも同じ処理で読み込みます。時刻の重なりや文字化けは、メッセージに示された SRT の行を修正してください。

再生成の確認は、同じホストで同じ描画エンジン、FFmpeg・libass、フォントファイルとフォント環境を使い、動画ファイル全体の SHA-256 を比較します。設定のファミリー名はフォント内の名前に合わせてください。新しいレシピでは、libass が代替フォントの選択を報告すると生成が失敗します。映像・音声の構成、画面サイズ、フレーム数と尺の検査を通ると、ジョブが完了になります。

フレーム計画の音符 `active` は、その時刻に発音中かを示します。バーと五線譜では音符の色を一定にして、演奏位置を縦の再生線で示します。

30・60 fps の合成キュー、修正前後の画素数、実曲と Edge での再現手順を検証記録にまとめています。[フレーム境界の検証記録](../../tests/TheoriaCast.Visual.Tests/karaoke-verification.md)

**長い曲のメモリと保存容量**　音源の上限は20分・128 MiB、ガイド譜は24,000音符です。Atelier は WAV 全体を一度に読み込むため、最大入力では音源バッファだけで128 MiBを使い、ハッシュ処理と転送にもメモリが必要です。五線譜は符幹・加線・臨時記号で描画命令が増え、最大付近では ASS が数十 MB 規模になる場合があります。サーバーでは組み立て中のバッファと完成した文字列が一時的に共存します。ASS の逐次書き出し、ブラウザーの分割読み込み・逐次ハッシュ、保存期限とジョブ一覧の索引は今後の対応項目です。
