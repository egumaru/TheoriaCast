# イントロ・ループ部・アウトロを組み立てる

接続元と素材を確認する。`get_music_versions` の selected が対象の曲棚で、API が 0.2.4 以降であることを確認する。`get_track` でテイクIDを確認する。以下のプロジェクト操作はすべて `mix_projects` の `input` に入れる。成功するたびに返る `head` を次の `expected` にする。

1. `{"action":"create","manifest":{"title":"イントロ・ループ・アウトロ","tracks":[]}}`。返った `project` を以後の操作に付ける。
2. `{"action":"place_clip","project":"P","expected":"H","clip":{"lane":"bgm","id":"intro","source":{"take":"T"},"start_sample":0,"in_sample":0,"out_sample":192000}}`。新しいレーンは自動作成される。整数はすべて48 kHzのフレーム数。トリムは素材内の半開区間で、配置はレーン先頭からのオフセット。既存の静止ミックスのレーンに追加すると、従来の音はクリップ `original` として残る。
3. 反復したい区間を `tune_loop({"request":{"track":"T","selection":{"start":4,"end":12,"crossfade_ms":30}}})` で解析する。返った `start,end,crossfade_ms` をそのまま `export_track({"request":{"track":"T","kind":"loop","loop":{…}}})` に渡す。`get_export` が done になったら `recipe.conditions.parameters.loop.LOOPLENGTH` が一周の正確なフレーム数。素材には書き出しジョブの `id` を使う。
4. `place_clip` に `clip:{"lane":"bgm","id":"body","source":{"take":"T","loop_job":"J"},"start_sample":192000}` を指定。`loop_clip` に `clip:{"lane":"bgm","id":"body","repeat_count":16}` を渡す。尺を直接決めるには `repeat_count` の代わりに `length_samples` を渡す。別のループ点を使うには新しい `loop_job` を同じ `loop_clip` に渡す。ループ点とクロスフェードを含む完成した一周を反復するので、その一周全体を選ぶ。
5. `place_clip` でアウトロを配置する。`repeat_count` を使った場合の開始位置は `body.start_sample + LOOPLENGTH × repeat_count`。`length_samples` を使った場合は `body.start_sample + length_samples`。重なったクリップは加算される。
6. 各操作の `clip` に `lane,id` を付ける。`move_clip` は `start_sample`、別レーンへ移す場合は `to_lane`。開始位置を省略すると曲中の時刻を維持する。`trim_clip` は `in_sample,out_sample`、`fade_clip` は `fade_in_samples,fade_out_samples`、`delete_clip` は `lane,id`。フェードは配置全体の両端にかかる。空レーンは無音。
7. マルチクリッピング対応のAtelierで保存したレシピは、同じ曲棚・テイクの `source:{"take":"T","short_recipe":recipe}` にオブジェクト全体を渡す。元音源のSHA-256とサンプルレートを照合して配置する。
8. `snapshot` に `label`、`render` に `project,expected` を渡し、返った `job` を `get_export` で確認。`recipe.result_track` が試聴待ちの曲。巻き戻しは `rollback` に `project,expected,revision`。復元後に再び書き出せばWAVのSHA-256を比較できる。`history` はクリップの設定と素材を版ごとに返す。
