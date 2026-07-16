# win-somnia

`win-somnia` は、指定した時間帯に Windows ワークステーションのロックを繰り返す、ログオンユーザー向けの PowerShell スクリプトです。既定の制限時間帯は 23:00 から翌 06:00、確認間隔は 5 秒です。

## 安全設計

- 実ロックは `win-somnia-monitor.ps1` に `-EnableLock` を明示した場合だけ有効です。引数なしの手動実行ではロックしません。
- `-DryRun` は実ロックを一切行わず、既定で 60 秒後に自動終了します。
- 各ループ、実ロックの直前、待機中は 1 秒ごとにキルスイッチを確認します。
- キルスイッチ `C:\temp\win-somnia-unlock.txt` が存在すると、ロックせず直ちに終了します。ファイルの内容は問いません。
- タスクは同一インスタンスを重複起動せず、現在のユーザー権限でのみ動作します。

## 最初に行う安全なテスト

Windows PowerShell 5.1 でリポジトリのディレクトリを開きます。実ロックやタスク登録のテストを始める前に、キルスイッチ用ディレクトリを用意してください。

```powershell
New-Item -ItemType Directory -Path C:\temp -Force
```

制限時間帯を現在時刻を含む値にしても、次のドライランは画面をロックせず 60 秒で終了します。`HH:mm` は実行時刻を挟む値に置き換えてください。

```powershell
.\win-somnia-monitor.ps1 -DryRun -TestDurationSeconds 60 -StartTime 'HH:mm' -EndTime 'HH:mm'
```

出力に `Lock would be requested` が約 5 秒おきに表示されることを確認します。短い構文・安全装置テストには次も使えます。

```powershell
# 3 秒で自動終了し、絶対にロックしない
.\win-somnia-monitor.ps1 -DryRun -TestDurationSeconds 3 -StartTime '00:00' -EndTime '23:59' -IntervalSeconds 1

# キルスイッチがあれば即終了する
New-Item -ItemType File -Path C:\temp\win-somnia-unlock.txt -Force
.\win-somnia-monitor.ps1 -EnableLock -StartTime '00:00' -EndTime '23:59'
Remove-Item -LiteralPath C:\temp\win-somnia-unlock.txt
```

> [!CAUTION]
> 手動テストで `-EnableLock` を付けないでください。実ロック試験が必要な場合も、別端末や別管理者セッションからキルスイッチを作成できる状態を先に確保してください。

## インストールと解除

現在のユーザーのログオン時に、非表示の Windows PowerShell プロセスとして起動するタスクを登録します。セットアップはモニターへ `-EnableLock` を渡すため、登録後は実際にロックされます。

```powershell
# 内容だけ確認（変更しない）
.\win-somnia-setup.ps1 -Action Install -WhatIf

# 既定値（23:00-06:00、5 秒間隔）で登録または更新
.\win-somnia-setup.ps1 -Action Install

# 時刻と間隔を指定して登録または更新
.\win-somnia-setup.ps1 -Action Install -StartTime '00:30' -EndTime '06:30' -IntervalSeconds 10

# 登録解除
.\win-somnia-setup.ps1 -Action Uninstall
```

タスク名は既定で `win-somnia` です。スクリプトを移動すると登録済みのパスは自動更新されないため、移動後にセットアップを再実行してください。

## 緊急停止（キルスイッチ）

ロックが繰り返される場合は、別の管理者セッション、PowerShell Remoting、または別の起動環境から次のファイルを作成します。

```powershell
New-Item -ItemType Directory -Path C:\temp -Force
New-Item -ItemType File -Path C:\temp\win-somnia-unlock.txt -Force
```

モニターは通常 1 秒以内に停止します。タスクスケジューラからタスクを停止しても構いません。

```powershell
Stop-ScheduledTask -TaskName win-somnia
```

キルスイッチが残っている限り、次回ログオンやタスク再実行でもモニターはロックせず終了します。再開するときだけファイルを削除してください。

```powershell
Remove-Item -LiteralPath C:\temp\win-somnia-unlock.txt
Start-ScheduledTask -TaskName win-somnia
```

キルスイッチの場所を変更する場合は、セットアップ時に `-KillSwitchPath` を指定します。場所は登録タスクの引数として保存されます。

## モニター引数

| 引数 | 既定値 | 説明 |
| --- | --- | --- |
| `-StartTime` | `23:00` | 制限開始（24 時間表記 `HH:mm`） |
| `-EndTime` | `06:00` | 制限終了。開始より早ければ日付をまたぐ |
| `-IntervalSeconds` | `5` | 判定・ロック間隔（1〜3600 秒） |
| `-KillSwitchPath` | `C:\temp\win-somnia-unlock.txt` | 存在時に即終了するファイル |
| `-EnableLock` | 無効 | 実ロックを明示的に有効化 |
| `-DryRun` | 無効 | 実ロックせず、時間制限付きで動作確認 |
| `-TestDurationSeconds` | `60` | ドライランの実行秒数（1〜3600 秒） |

開始時刻と終了時刻が同じ設定は、意図しない 24 時間ロックを防ぐためエラーにしています。
