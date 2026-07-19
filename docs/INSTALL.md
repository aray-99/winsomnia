# winsomnia installation

## Windows desktop release

GitHub Release の `winsomnia-<version>-desktop.zip` を使用します。この単一パッケージに WPF Desktop、Engine、ユーザー単位 Setup が含まれます。旧 PowerShell script package は v0.3 で廃止されています。

1. ZIP を通常ユーザーが書き込めるフォルダーへ展開します。
2. 展開された `winsomnia-<version>-desktop` フォルダーの構成を保ち、`Winsomnia.Setup.exe` を実行します。
3. スタートメニューから winsomnia を開き、制限予定を確認します。
4. Diagnostics の安全テストを実行します。このテストはロックしません。
5. 内容を確認した上で `Activate / 有効化` を選びます。

Setup は `app` フォルダーから Desktop と Engine を検証・配置し、タスクを無効、Engine を disarm、enable marker を欠落させた状態で終了します。Setup 自体が実ロックやタスク開始を行うことはありません。

## Verify the installation

インストール先は `%LOCALAPPDATA%\Programs\winsomnia` です。`VERSION` が Release と一致し、タスク `winsomnia` が無効で、enable marker が存在しないことを有効化前に確認できます。

```powershell
Get-Content "$env:LOCALAPPDATA\Programs\winsomnia\VERSION"
Get-ScheduledTask -TaskName winsomnia | Select-Object TaskName, State
Test-Path -LiteralPath C:\temp\winsomnia-lock-enabled.json
```

最後の結果は安全停止中なら `False` です。失敗時はタスクを有効化せず、[EMERGENCY.md](EMERGENCY.md) を参照してください。

## Updating

既存の winsomnia を Desktop の `Pause / 一時停止` で停止してから、新しい ZIP を別フォルダーへ展開して `Winsomnia.Setup.exe` を実行します。

v0.3 Setup は更新前に旧互換 kill switch を作成・検証し、`winsomnia` と `win-somnia` の両タスクを停止・無効化し、Engine と旧 monitor が残っていないことを検証します。その後、旧設定だけを disarm の schema-v3 state へ移行します。旧 state/config は移行元として読み取り専用で残り、旧 PowerShell runtime は新パッケージへ含まれません。

配置は完全な payload の staging と検証後に行われます。中断された置換は journal から復旧され、失敗時は安全 barrier を再確立して非ゼロで終了します。復旧確認は、旧互換 kill switch が存在し、enable marker が存在せず、Engine が disarm、両タスクが無効または欠落であることです。

## Uninstalling

インストール先の外に展開した新しい Release package から次を実行します。

```powershell
.\Winsomnia.Setup.exe uninstall
```

インストール済み Setup は実行中の自分自身を安全に削除できないため self-uninstall を拒否します。Uninstall は同じ safety barrier を適用してからタスク、shortcut、binary を削除し、旧互換 kill switch とユーザーデータは保持します。
