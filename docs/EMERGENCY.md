# Emergency recovery

画面ロックが繰り返される、設定時刻を過ぎてもロックが止まらない、またはモニターを終了できない場合の復旧手順です。

## 最短手順：別の管理者アカウント

1. ロック画面または Ctrl+Alt+Delete 画面から「ユーザーの切り替え」を選択します。
2. あらかじめ作成した非常用ローカル管理者アカウントへログインします。
3. Windows TerminalまたはPowerShellを「管理者として実行」します。
4. 次を実行します。

~~~powershell
New-Item -ItemType Directory -Path C:\temp -Force
New-Item -ItemType File -Path C:\temp\win-somnia-unlock.txt -Force
Stop-ScheduledTask -TaskName winsomnia -ErrorAction SilentlyContinue
Disable-ScheduledTask -TaskName winsomnia
~~~

5. 元のユーザーへ切り替えてログインします。

キルスイッチは現在表示中のWindowsロック画面を解除しません。作成後に通常どおり一度ログインすると、再ロックが止まります。

## 復旧確認

非常用管理者アカウントで次を確認します。

~~~powershell
Test-Path C:\temp\win-somnia-unlock.txt
Get-ScheduledTask -TaskName winsomnia |
    Select-Object TaskName, State
~~~

Test-PathがTrueで、タスクがDisabledなら安全停止状態です。原因を確認するまでキルスイッチを削除しないでください。

## 別アカウントを使えない場合

サインイン画面でShiftキーを押しながら「電源」→「再起動」を選択します。その後、「トラブルシューティング」→「詳細オプション」→「スタートアップ設定」→「再起動」→セーフモードを選択します。

セーフモードではPINや生体認証ではなく、アカウントのパスワードが必要になる場合があります。BitLockerが有効なPCでは回復キーが必要になる場合もあります。

Microsoft公式資料：

- [Windowsのサインイン問題とセーフモード](https://support.microsoft.com/en-gb/windows/troubleshoot-problems-signing-in-to-windows-298cfd5f-df1f-c66b-36ad-f2a61a73baad)
- [Windows回復環境](https://support.microsoft.com/en-us/windows/windows-recovery-environment-0eb14733-6301-41cb-8d26-06a12b42770b)

セーフモードへログイン後、管理者PowerShellでキルスイッチを作成し、タスクを無効化します。

~~~powershell
New-Item -ItemType Directory -Path C:\temp -Force
New-Item -ItemType File -Path C:\temp\win-somnia-unlock.txt -Force
Disable-ScheduledTask -TaskName winsomnia
~~~

## 再起動だけでは停止にならない

キルスイッチが存在しない状態で通常再起動すると、元のユーザーがログオンした時点でモニターが再起動します。必ずキルスイッチ作成またはタスク無効化まで行ってください。

## 安全を確認して再開する

原因を解決し、ドライランが成功してから元のユーザーで実行します。

~~~powershell
Enable-ScheduledTask -TaskName winsomnia
.\winsomnia.ps1 test -TestDurationSeconds 60
.\winsomnia.ps1 resume
~~~

非常用アカウントは日常利用せず、パスワードはスマートフォンのパスワードマネージャー、または封印した紙などPC外に保管してください。
