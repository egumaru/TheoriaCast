# TheoriaCast
Theoria Castは、AIKernel.Theoria上に構築される、Local-first型の遠隔監督AIキャスティングシステムである。
AI推論、音声合成、アバター描画、配信処理は利用者のEdge環境で実行し、遠隔地のOperatorは軽量なRelayを介して承認・編集・停止を行う。
Relayは命令を中継するが、実行の最終権限は常にEdgeに残る。通信、認証、データ鮮度、承認期限のいずれかを検証できない場合、新しいAI発話を開始しない。
