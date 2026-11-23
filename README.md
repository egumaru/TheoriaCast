# TheoriaCast
Theoria Castは、AIKernel.Theoria上に構築される、Local-first型の遠隔監督AIキャスティングシステムである。
AI推論、音声合成、アバター描画、配信処理は利用者のEdge環境で実行し、遠隔地のOperatorは軽量なRelayを介して承認・編集・停止を行う。
Relayは命令を中継するが、実行の最終権限は常にEdgeに残る。通信、認証、データ鮮度、承認期限のいずれかを検証できない場合、新しいAI発話を開始しない。
なお、まだ開発中のため、公開に向けて準備中。

## TheoriaCast.Music

TheoriaCast is an integrated broadcasting environment under development. Music is its independently distributed composition tool. Install the API alongside your own generation engine on a local or cloud GPU, then import the server's connection file into Sound Atelier.

TheoriaCast 本体は開発中です。付属の作曲ツール Music を先行して配布します。手元またはクラウドの GPU 機にエンジンと API を用意し、Atelier に接続ファイルを読み込みます。

- [日本語インストールガイドと操作マニュアル](src/TheoriaCast.Music/README.md)
- [English installation and user guide](src/TheoriaCast.Music/README.en.md)
- [Windows / Linux releases](https://github.com/egumaru/TheoriaCast/releases)
- [Review and verification / レビュー対応](src/TheoriaCast.Music/REVIEW.md)
- [Codex and Claude Code composition and editing skill](.agents/skills/theoriacast-music/SKILL.md)
- [公式サイト / Official site](https://theoriacast.online/)

## TheoriaCast.Contracts

The shared C# data contracts for Unity and .NET are available as **org.theoriacast.contracts 1.0.0**. Unity と .NET の間でシーン、アクション、レンダラーの状態を交換するための共通型定義です。 [Installation and usage / 導入手順](src/TheoriaCast.Contracts/README.md)

### Build and test

Use the .NET 10 SDK. The API uses the AIKernel 0.1.4 distributed hosting provider.

```sh
dotnet restore TheoriaCast.Music.slnx
dotnet build TheoriaCast.Music.slnx -c Release --no-restore
dotnet run --project tests/TheoriaCast.Music.Tests -c Release --no-build
python src/TheoriaCast.Music/checks/singing_check.py
```

To build four self-contained distribution archives, run `python src/TheoriaCast.Music/package.py` with Python 3.11+ and the .NET 10 SDK. The release contains two separate applications on each OS. Model weights and external music engines are supplied by each user.

The Music source is licensed under [Apache License 2.0](LICENSE). Each binary archive includes notices for its distributed dependencies.
