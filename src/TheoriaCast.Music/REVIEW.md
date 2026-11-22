# Music review and verification / レビュー対応記録

2026-11-23 · TheoriaCast.Music 0.1.0 · AIKernel 0.1.4

元のレビュー番号に沿った変更と検査方法です。Each row identifies the change and how it was checked. Commands use the Music-only GitHub checkout.

| # | 対応 / Change | 確認方法 / Check |
|---|---|---|
| 1 | 通信と転送を .NET ワーカーに一本化。Windows/Linux share the bearer-authenticated, chunked worker. | GPU テイクと OS 別配布物で実行。Native delivery and distribution tests. |
| 2 | 期限切れ3回で failed。優先作業による返却は回数に含めない。Lease loss has a three-attempt limit. | C# expiry / voluntary requeue regression. |
| 3 | MCP の採用は codex、画面経由は human。Review attribution follows the caller. | C# persisted provenance assertions. |
| 4 | 宣言済みの対応種別で機器を選択。Unhandled jobs return 400. | C# unsupported-engine regression. |
| 5 | マニフェストでエンジン・改訂・ライセンス・実行コマンドを指定。Generation adapters are independent of queue transport. | External-command contract, reference adapters on GPU and historical profile conversion. |
| 6 | 重複 ZIP を拒否し、担当を照合後に failed と記録。Malformed deliveries get a logged 400. | C# endpoint test with duplicate entry. |
| 7 | Windows 読取で削除共有を許可し、一時的な置換競合は再試行。Progress replacement tolerates transient contention. | Code inspection and Windows worker execution. The original collision was intermittent. |
| 8 | 年の接尾辞を持つ年号と「気に入る」を修正。Bare four-digit kanji remain lyrics. | Three singing tests, including both reported cases. |
| 9 | 待機ジョブと空き判定を通過してから共有ロックを取得。An idle polling cycle does not acquire the GPU lock. | Worker control-flow inspection and preemption exercise. |
| 10 | トラック・MCP は再生用 URL を返す。Absolute media paths stay on the host. | C# serialization check and browser playback. |
| 11 | トリム、差し替え、字幕、フェード、つなぎ目を既知の実尺と照合。Reject out-of-range edits at submission. | C# invalid trim, fade and crossfade tests. |
| 12 | worker-token でスコープ別の資格情報を個別出力。Private token provisioning and optional service environment file. | Native token export and authenticated workers. |
| 13 | 起動例を API/Atelier に更新。Four separate self-contained archives and bilingual installation instructions. | Extract and start both programs on Windows and Linux. |
| 14 | Music 専用 C# 回帰試験を追加。Runnable without a GPU, browser or Codex. | `dotnet run --project tests/TheoriaCast.Music.Tests -c Release` |
| 15 | 共通契約を Unity と Core の外に配置。The common contract lives in a local Unity package. | Full solution build and a separate Music-only checkout build. |
| 16 | 実行環境・シード・引数・FFmpeg・音声ハッシュを保存。Replay verifies the master WAV. | GPU generation → ZIP import → replay for BGM and vocals. |
| 17 | 通常の歌生成ではモデルをテイク間で再利用。The low-memory profile releases generator weights before decoding and reloads for a retry. | Adapter lifetime inspection and vocal generation. |
| 18 | 2パスでラウドネスを計測・補正し測定値を記録。Silence without finite measurements bypasses normalization. | GPU generation and verified audio hashes. |
| 19 | 親の調整履歴を合成・差し替え結果に保持。Parent track IDs, hashes and processing history are retained. | Mix lineage and waveform replacement checks. |
| 20 | 変更番号が動いた一覧だけ再取得、波形は範囲とファイル更新時刻でキャッシュ。Revision polling and bounded waveform cache. | Browser refresh and C# waveform range/cache tests. |
| 21 | 外部レンダラーの job.json は ID と spec。The lease remains in the .NET worker. | Staging code inspection. |
| 22 | 複数 URL、明示的なワーカー起動、自分の古い作業領域の回収。Multiple listeners and profile-specific scratch cleanup. | API startup checks and source inspection. |
| 23 | localhost 内の信頼境界とトークンの扱いを英日マニュアルに記載。Local processes share the user's Atelier session. | Loopback binding and authenticated forwarding. |
| 24 | 配布物ごとに実際の NuGet 依存、ライセンス、第三者告知を収録。Archives include notices for their own dependency graph. | Inspect four archive manifests, checksums and included notices. |

TheoriaCast の本体と Unity 用アセットは Music の配布対象外です。The GitHub release contains only the Music applications and their integration material. Model weights, third-party generation engines and artwork are supplied separately.
