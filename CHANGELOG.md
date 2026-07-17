# Changelog

このプロジェクトの重要な変更を記録します。バージョン番号は Semantic Versioning に従います。

## [Unreleased]

### Added

- Windows 11向けWPF設定アプリと読み取り専用トレイ状態表示
- 24時間保留設定、例外日、一時解除クレジットを管理するschema v2
- Focus Appから将来利用できる期限付きロックセッションEngineとローカルIPC v1
- ユーザー単位セットアップとv0.1.0設定移行

## [0.1.0] - 2026-07-17

### Added

- AGENTS、コントリビューションガイド、PRテンプレートによる安全な開発規約
- Git Flow、Conventional Commits、個人情報、リリースタグを検査するCIポリシー

### Changed

- ツール名と公開ファイル名を `winsomnia` に統一
- リリース候補で夜間のロック・再ロック、終了時刻、スリープ復帰、バッテリー動作を検証

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
