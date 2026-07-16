# Changelog

このプロジェクトの重要な変更を記録します。バージョン番号は Semantic Versioning に従います。

## [Unreleased]

### Added

- AGENTS、コントリビューションガイド、PRテンプレートによる安全な開発規約
- Git Flow、Conventional Commits、個人情報、リリースタグを検査するCIポリシー

### Changed

- ツール名と公開ファイル名を `winsomnia` に統一

### Fixed

- GUIプロセスとして起動したLockWorkStationヘルパーの終了コードを安全に取得
- 配布ZIPへ緊急復旧手順とREADMEから参照する文書を収録

## [0.1.0-rc.1] - 2026-07-16

### Added

- 指定時間帯にWindowsワークステーションを繰り返しロックするモニター
- 外部キルスイッチ、実ロックの明示的有効化、時間制限付きドライラン
- ログオン時に非表示で起動するタスクの登録・解除
- 統合CLI、対話メニュー、共有JSON設定、動作ログ
- Pester安全テスト、PSScriptAnalyzer、Windows GitHub Actions CI
- ZIPパッケージとSHA-256チェックサムの生成
- MIT License
- スマートフォンから参照できる緊急復旧手順
