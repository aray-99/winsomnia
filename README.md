# winsomnia

Windowsの夜更かしを防ぐため、決めた時間帯に画面ロックを繰り返すPowerShellツールです。

既定では毎日 **23:00〜06:00** に動作します。ログオン中は非表示で待機し、制限時間中にサインインしても約5秒後に再ロックします。

> [!IMPORTANT]
> ロックが止まらない場合は、スマートフォンなど別端末から [緊急復旧手順](docs/EMERGENCY.md) を開いてください。

## まず使う

Windows PowerShellでリポジトリを開きます。

~~~powershell
cd <winsomniaを展開またはcloneしたフォルダー>
~~~

初回設定または設定変更：

~~~powershell
.\winsomnia.ps1 setup
~~~

現在の状態を確認：

~~~powershell
.\winsomnia.ps1 status
~~~

実運用を開始：

~~~powershell
.\winsomnia.ps1 resume
~~~

引数なしで起動すると、同じ操作を選べる対話メニューを表示します。

~~~powershell
.\winsomnia.ps1
~~~

## 日常操作

| 目的 | コマンド |
| --- | --- |
| 状態を見る | `.\winsomnia.ps1 status` |
| 一時停止する | `.\winsomnia.ps1 pause` |
| 再開する | `.\winsomnia.ps1 resume` |
| ロックなしで試す | `.\winsomnia.ps1 test` |
| 最新ログを見る | `.\winsomnia.ps1 logs` |
| アンインストール | `.\winsomnia.ps1 uninstall` |

設定変更の例：

~~~powershell
.\winsomnia.ps1 setup `
  -StartTime '23:30' `
  -EndTime '06:30' `
  -IntervalSeconds 10
~~~

## 安全装置

winsomniaは実際にユーザーをロックするため、停止手段を複数用意しています。

- `test`は実際にロックせず、短時間で自動終了します。
- 実ロックはモニターへ`-EnableLock`を明示した場合だけ有効です。
- キルスイッチが存在すると、ロックせず通常1秒以内に終了します。
- 開始時刻と終了時刻が同じ設定は、24時間ロックを避けるため拒否します。
- タスクの重複起動を防ぎ、異常終了時は最大3回再起動します。

既定のキルスイッチ：

~~~text
C:\temp\win-somnia-unlock.txt
~~~

キルスイッチ名は旧バージョンと緊急手順の互換性を保つため、ハイフン付きのまま維持しています。

通常はファイルを直接操作せず、次を使ってください。

~~~powershell
.\winsomnia.ps1 pause
.\winsomnia.ps1 resume
~~~

## Windowsラップトップでの動作

- セットアップを実行したユーザーのログオン時に非表示で起動します。
- 制限時間外もバックグラウンドで時刻を監視します。
- 再起動後は、ログオンすると自動的に監視を再開します。
- スリープ中は処理が止まり、復帰後に時刻判定を再開します。
- バッテリー駆動へ切り替わっても停止しません。
- キルスイッチがある状態でログオンすると、モニターは安全に終了します。

## 保存場所

| 内容 | 既定の場所 |
| --- | --- |
| 設定 | `%LOCALAPPDATA%\winsomnia\config.json` |
| ログ | `%LOCALAPPDATA%\winsomnia\winsomnia.log` |
| 緊急停止 | `C:\temp\win-somnia-unlock.txt` |
| タスク名 | `winsomnia` |

設定ファイルには制限時刻、再ロック間隔、キルスイッチ、ログ保存先を記録します。不正な時刻や相対パスを検出した場合は、ロックせずエラー終了します。

## 開発とリリース

~~~powershell
# 静的解析
Invoke-ScriptAnalyzer -Path . -Recurse -Severity Warning,Error

# テスト
Invoke-Pester -Path .\tests -Output Detailed

# ZIPとSHA-256を生成
.\build-release.ps1
~~~

開発規約は [CONTRIBUTING.md](CONTRIBUTING.md)、詳しいリリース工程は [RELEASE.md](RELEASE.md)、変更履歴は [CHANGELOG.md](CHANGELOG.md) を参照してください。

## ライセンス

[MIT License](LICENSE)
