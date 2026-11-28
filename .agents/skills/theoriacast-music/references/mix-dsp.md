# トラックの音量・音色・残響を調整する

Music API 0.2.5以降。MCPの `mix_projects` を使い、対象の曲棚とプロジェクトを確認する。各変更には `project` と、直前に返った `head` を `expected` として付ける。競合が返ったら `get` で現在版を読み直す。

1. `render` の `job` を `get_export` で追い、完了後の `recipe.analysis` を読む。 `peak_dbfs.left/right/overall` は書き出したPCMのサンプルピーク、`peak_before_clip` は量子化前の最大絶対値（1がフルスケール）、`clipped_samples` は ±1 を超えた左右サンプルの合計。無音のピーク値は null。ステレオ48 kHzの `integrated_lufs` はK特性、400 ms窓、100 ms間隔、絶対・相対ゲートで算出する。400 ms未満やゲート通過区間がない場合は null。
2. 比較元に名前を付けるには `snapshot` に `label` を渡す。例えばレーン `music` の音量を下げるには次の引数を送る。

```json
{"input":{"action":"set_dsp","project":"P","expected":"H","dsp":{"lane":"music","id":"mixer","parameters":{"gain_db":-6}}}}
```

3. `add_dsp` でレーン内で一意の `id`、下表の `type`、`parameters` を渡す。例えば `{"lane":"music","id":"peak","type":"limiter","parameters":{"ceiling_db":-1.5}}`。省略したパラメータも既定値がレシピに記録される。
4. `set_dsp` で指定パラメータを更新。`move_dsp` は `index`（移動後の0始まりの順序）、`delete_dsp` は `lane,id` を使う。`revision.manifest.tracks[].dsp` の先頭から順に処理される。常設の `mixer` は従来のレーンの `gain_db,pan,invert_polarity` を参照し、位置を動かせる。ミュートとソロはレーン全体に適用する。
5. 再レンダーして同じ3項目を比較する。リミッタのシーリングはその挿入位置でのサンプルピークに作用する。後段のエフェクト、他レーンの加算、マスター音量を含む完成音は `analysis` で確かめる。試聴は `recipe.result_track`。著者が採用を判断する。
6. `rollback` に戻す版を渡せば順序とパラメータが復元される。過去のレンダーは `regenerate` にその版を渡し、`recipe.verification.wav_sha256` を比較する。

| type | parameters の単位と既定値 |
| --- | --- |
| gain_pan | gain_db 0 dB、pan 0（左 −1、右 +1） |
| hpf | frequency_hz 80 Hz、q 約0.707 |
| eq | ピーキングEQ。frequency_hz 1000 Hz、gain_db 0 dB、q 1 |
| compressor | threshold_db −18 dB、ratio 4、attack_ms 10 ms、release_ms 100 ms。左右連動 |
| limiter | ceiling_db −1 dBFS。左右連動 |
| reverb | decay_s 1.2秒（60 dB減衰）、room_size 0.5（0〜1）、wet 0.2、dry 1（独立した振幅係数）、predelay_ms 15 ms。残響のあるレーンは最後の入力後も約90 dB減衰まで書き出す |

測定仕様の参照先は [ITU-R BS.1770-5 Annex 1](https://www.itu.int/rec/R-REC-BS.1770-5-202311-I/)。
