# Emergency recovery

画面ロックが繰り返される場合は、利用中のバージョンに対応する安全操作を行います。バージョンが分からない場合は、両方の操作を行ってください。

## 重要な制限

- v0.3 の enable marker 削除は、それ以降のロック認可を拒否しますが、state の `Armed=false` を永続化しません。サインインできたら Desktop の `Pause / 一時停止` も実行してください。
- marker や旧 kill switch は、現在表示中のロック画面を解除しません。
- marker 削除は、すでに発行された非同期の `LockWorkStation` 要求を取り消しません。削除直後に一度ロックされる可能性があります。
- `LockWorkStation` の成功値やログは、画面が実際にロックされた証拠ではありません。
- 以下の WinRE、別アカウント、Safe Mode 経路は端末構成に依存します。Issue #38 の制御実機試験が完了するまでは、到達可能または実証済みとは扱いません。

Microsoft 公式資料:

- [LockWorkStation function](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-lockworkstation)
- [Windows Recovery Environment](https://support.microsoft.com/en-US/Windows/Experience/Backup-Recovery/windows-recovery-environment)
- [Windows startup settings](https://support.microsoft.com/en-us/windows/experience/startup-boot/windows-startup-settings)

## サインイン済みの Windows

管理者 PowerShell で、バージョンに応じた手順を実行します。

### v0.3

次の2行をそのまま実行します。

```powershell
Remove-Item -LiteralPath 'C:\temp\winsomnia-lock-enabled.json' -Force -ErrorAction SilentlyContinue
Test-Path -LiteralPath 'C:\temp\winsomnia-lock-enabled.json'
```

期待結果:

```text
False
```

`False` でなければ安全停止を確認できていません。タスクを有効化しないでください。

marker が `False` で Engine がまだ動作している間に、Desktop の `Pause / 一時停止` を実行します。画面上の操作結果が成功で、`Armed: No`、認可状態が `Disarmed` であることを目視確認します。これが `Armed=false` を永続化する通常の手順です。その後、管理者 PowerShell でタスクを停止・無効化し、状態と Engine プロセスを確認します。

```powershell
$ErrorActionPreference = 'Stop'
Stop-ScheduledTask -TaskName 'winsomnia' -ErrorAction SilentlyContinue
Disable-ScheduledTask -TaskName 'winsomnia'
Get-ScheduledTask -TaskName 'winsomnia' | Select-Object TaskName, State
@(Get-Process | Where-Object ProcessName -EQ 'Winsomnia.Engine').Count
```

タスクの `State` は `Disabled`、最後の Engine 件数は `0` である必要があります。エラー、別の状態、または0以外の件数が出た場合は完了と扱いません。MainWindow を閉じても Desktop tray は残る設計です。Desktop はロック API を所有しないため、tray の終了はロック安全性の必須条件ではありません。必要なら通常の Windows プロセス終了操作で終了できます。

Desktop または IPC が利用できない場合は、marker 削除とタスク停止・無効化までを緊急 containment として行います。この状態では以後の認可を拒否できますが、state が永続的に `Armed=false` になったとは断定できません。タスクを無効のままにし、後で Setup の safety barrier を実行するか、Desktop を手動起動して `Pause / 一時停止` の目視確認を完了してから Desktop を停止してください。内部 Engine CLI は復旧手順として公開・使用しません。

### v0.2.x

v0.2.x は肯定的 marker を認識しないため、旧 kill switch を作成します。

```powershell
New-Item -ItemType Directory -LiteralPath 'C:\temp' -Force | Out-Null
New-Item -ItemType File -LiteralPath 'C:\temp\win-somnia-unlock.txt' -Force | Out-Null
Test-Path -LiteralPath 'C:\temp\win-somnia-unlock.txt'
$ErrorActionPreference = 'Stop'
$taskNames = @('winsomnia','win-somnia')
$allTasks = @(Get-ScheduledTask)
foreach ($taskName in $taskNames) {
    $task = $allTasks | Where-Object TaskName -EQ $taskName
    if ($null -eq $task) {
        [pscustomobject]@{ TaskName = $taskName; State = 'MISSING' }
        continue
    }
    $task | Stop-ScheduledTask
    $task | Disable-ScheduledTask | Out-Null
}
$allTasks = @(Get-ScheduledTask)
foreach ($taskName in $taskNames) {
    $task = $allTasks | Where-Object TaskName -EQ $taskName
    if ($null -eq $task) {
        [pscustomobject]@{ TaskName = $taskName; State = 'MISSING' }
    }
    else {
        $task | Select-Object TaskName, State
    }
}
@(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -In 'powershell.exe','pwsh.exe' -and
    $_.CommandLine -Match 'win-somnia|winsomnia-monitor'
}).Count
```

`Test-Path` の期待結果は `True`、`winsomnia` と `win-somnia` はそれぞれ `Disabled` または `MISSING` です。最後の件数は winsomnia の旧 PowerShell runtime を対象とし、`0` である必要があります。エラー、その他の状態、または0以外の件数が出た場合は完了と扱いません。原因を確認するまで旧 kill switch を削除しません。

### バージョンが分からない

両方の安全操作を行い、v0.3 の `winsomnia` と v0.2.x の `win-somnia` の両タスクを停止・無効化します。コマンド失敗を抑制せず、各タスクを `Disabled` または `MISSING` として明示的に確認します。

```powershell
New-Item -ItemType Directory -LiteralPath 'C:\temp' -Force | Out-Null
Remove-Item -LiteralPath 'C:\temp\winsomnia-lock-enabled.json' -Force -ErrorAction SilentlyContinue
New-Item -ItemType File -LiteralPath 'C:\temp\win-somnia-unlock.txt' -Force | Out-Null
Test-Path -LiteralPath 'C:\temp\winsomnia-lock-enabled.json'
Test-Path -LiteralPath 'C:\temp\win-somnia-unlock.txt'
$ErrorActionPreference = 'Stop'
$taskNames = @('winsomnia','win-somnia')
$allTasks = @(Get-ScheduledTask)
foreach ($taskName in $taskNames) {
    $task = $allTasks | Where-Object TaskName -EQ $taskName
    if ($null -eq $task) {
        [pscustomobject]@{ TaskName = $taskName; State = 'MISSING' }
        continue
    }
    $task | Stop-ScheduledTask
    $task | Disable-ScheduledTask | Out-Null
}
$allTasks = @(Get-ScheduledTask)
foreach ($taskName in $taskNames) {
    $task = $allTasks | Where-Object TaskName -EQ $taskName
    if ($null -eq $task) {
        [pscustomobject]@{ TaskName = $taskName; State = 'MISSING' }
    }
    else {
        $task | Select-Object TaskName, State
    }
}
$v03ProcessCount = @(Get-Process | Where-Object ProcessName -EQ 'Winsomnia.Engine').Count
$legacyProcessCount = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -In 'powershell.exe','pwsh.exe' -and
    $_.CommandLine -Match 'win-somnia|winsomnia-monitor'
}).Count
[pscustomobject]@{ V03Processes = $v03ProcessCount; LegacyProcesses = $legacyProcessCount }
```

最初の `Test-Path` は `False`、次は `True` である必要があります。両タスクは `Disabled` または `MISSING`、最後の `V03Processes` と `LegacyProcesses` はどちらも `0` である必要があります。例外、その他の状態、または0以外の件数が出た場合は安全停止を確認できていません。v0.3 の state を durable disarm したことは、この不明バージョン手順だけでは断定せず、後で Setup の safety barrier または Desktop の `Pause / 一時停止` で確認します。

## サインイン画面または別の管理者

利用可能なら「ユーザーの切り替え」から、あらかじめ用意したローカル管理者へサインインし、管理者 PowerShell で上記手順を実行します。アカウントの有無、端末設定、資格情報によって利用できない場合があります。別アカウントから必ず操作できることや、ロックが特定セッションだけに影響することを前提にしないでください。

## Windows Recovery Environment の Command Prompt

サインイン画面の電源メニューで利用可能なら、Shift を押しながら「再起動」し、「トラブルシューティング」→「詳細オプション」→「コマンド プロンプト」へ進みます。表示されない場合は、Microsoft の Windows RE 文書にある回復ドライブ、インストールメディア、または端末メーカー固有の手段が必要です。強制的な起動失敗はデータ損失の危険があるため通常手順として案内しません。

WinRE では Windows ボリュームが `C:` とは限りません。まず確認します。

```bat
diskpart
list volume
exit
```

候補ごとに、`D:` を実際のボリューム文字へ置き換えて確認します。

```bat
dir D:\Windows\System32\Config\SYSTEM
```

以下では確認済みの Windows ボリュームを `D:` とします。

### v0.3

```bat
set "OSVOL=D:"
del /f /q "%OSVOL%\temp\winsomnia-lock-enabled.json" 2>nul
if not exist "%OSVOL%\temp\winsomnia-lock-enabled.json" echo MARKER_ABSENT
```

`MARKER_ABSENT` が表示されなければ安全停止を確認できていません。

### v0.2.x

```bat
set "OSVOL=D:"
if not exist "%OSVOL%\temp" md "%OSVOL%\temp"
type nul > "%OSVOL%\temp\win-somnia-unlock.txt"
if exist "%OSVOL%\temp\win-somnia-unlock.txt" echo LEGACY_SWITCH_PRESENT
```

### バージョンが分からない

```bat
set "OSVOL=D:"
if not exist "%OSVOL%\temp" md "%OSVOL%\temp"
del /f /q "%OSVOL%\temp\winsomnia-lock-enabled.json" 2>nul
type nul > "%OSVOL%\temp\win-somnia-unlock.txt"
if not exist "%OSVOL%\temp\winsomnia-lock-enabled.json" echo MARKER_ABSENT
if exist "%OSVOL%\temp\win-somnia-unlock.txt" echo LEGACY_SWITCH_PRESENT
```

WinRE の `schtasks` はオフライン Windows のタスクを無効化する手順として使いません。通常 Windows へ戻ってサインインできたら、PowerShell の「バージョンが分からない」手順で両タスクを停止・無効化し、v0.3 では Desktop の `Pause / 一時停止` も実行します。

## BitLocker、ドライブ文字、Safe Mode の条件

- 暗号化されたボリュームでは、Windows RE のツール利用時に BitLocker 回復キーが必要になる場合があります。回復キーを確認できない状態で保護を解除しないでください。
- 必要なら `manage-bde -status` で確認し、Microsoft の [manage-bde unlock](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/manage-bde-unlock) に従います。ボリューム文字と回復キーを推測しません。
- WinRE のドライブ文字は通常起動時と異なる場合があります。`diskpart` の `list volume` と Windows ディレクトリの確認を省略しません。
- Safe Mode with Command Prompt は、Windows RE の「スタートアップ設定」に選択肢があり、その端末で利用できる場合だけ使用します。PIN や生体認証ではなくパスワードが必要になる場合があります。
- Safe Mode に到達しても、タスク、資格情報、暗号化、端末ポリシーの違いにより同じ操作ができるとは限りません。証跡のない経路を保証しません。

## 復旧後の確認

サインイン後、該当バージョンの手順にある `Test-Path`、タスク状態、プロセス件数を再確認します。v0.3 の最終安全状態は marker が `False`、Desktop の目視結果または Setup safety barrier で Engine が `Armed=false`、タスクが `Disabled` または `MISSING`、Winsomnia.Engine の件数が `0` です。v0.2.x では旧 kill switch が `True`、両タスクが `Disabled` または `MISSING`、旧 winsomnia monitor の件数が `0` であることも確認します。Desktop tray は残っていてもロック安全性へ影響しません。不確実なら再開せず、旧 kill switch を保持します。

## 再開

原因究明と承認済みの手動試験が完了するまで再開しません。marker 作成、タスク有効化、実ロック、再起動はそれぞれ直前に明示的な承認が必要です。
