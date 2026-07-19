# winsomnia

winsomnia は、決めた時間帯に Windows のワークステーションを繰り返しロックし、夜更かしを防ぐデスクトップアプリです。既定の制限時間は毎日 **23:00〜06:00** です。

> [!IMPORTANT]
> ロックが止まらない場合は、スマートフォンなど別端末から [緊急復旧手順](docs/EMERGENCY.md) を開いてください。

## インストール

GitHub Release の `winsomnia-<version>-desktop.zip` を展開し、フォルダー直下の `Winsomnia.Setup.exe` を実行します。`app` フォルダーは Setup と同じ場所に保ってください。

Setup はユーザーごとのインストールを行い、タスクを無効、Engine を disarm の安全停止状態にして終了します。スタートメニューから winsomnia を開き、予定を確認して Diagnostics の安全テストを実行してから有効化してください。詳しくは [インストールガイド](docs/INSTALL.md) を参照してください。

## 操作

設定画面では、現在の状態確認、24時間後に反映する予定変更、例外日の予約、安全テスト、有効化、一時停止を行えます。通知領域アイコンは状態表示専用です。

v0.3 では `Winsomnia.Engine` がロック状態と Windows のロック API を単独で所有します。旧 PowerShell monitor、setup、config runtime、管理 CLI は廃止しました。旧版からの更新時だけ、Setup が旧タスクと monitor を検出して安全停止し、設定を disarm 状態へ移行します。

## 安全装置

- 実ロックには Engine の `--enable-lock`、schema-v3 の `Armed=true`、固定の肯定的 enable marker がすべて必要です。
- marker の欠落、破損、ID 不一致、読み取り失敗はロックを拒否します。
- 一時停止は Engine を永続的に disarm し、marker を失効させます。
- Setup、更新、アンインストールはタスクを停止・無効化し、Engine が disarm であることを検証します。
- 自動試験は fake locker を使い、Windows のロック API を呼びません。

固定 marker:

```text
C:\temp\winsomnia-lock-enabled.json
```

状態ファイルは `%LOCALAPPDATA%\winsomnia\state-v3.json`、インストール先は `%LOCALAPPDATA%\Programs\winsomnia`、タスク名は `winsomnia` です。設計詳細は [アーキテクチャ](docs/ARCHITECTURE.md)、IPC 契約は [IPC v2](docs/IPC.md) を参照してください。

## 開発とリリース

```powershell
Invoke-ScriptAnalyzer -Path . -Recurse -Severity Warning,Error
Invoke-Pester -Path .\tests -CI -Output Detailed
.\scripts\Test-RepositoryPolicy.ps1
.\build-release.ps1
```

`build-release.ps1` は Desktop、Engine、Setup を含む単一 ZIP と SHA-256 を `dist` に生成します。開発規約は [CONTRIBUTING.md](CONTRIBUTING.md)、リリース工程は [RELEASE.md](RELEASE.md)、変更履歴は [CHANGELOG.md](CHANGELOG.md) を参照してください。

## ライセンス

[MIT License](LICENSE)
