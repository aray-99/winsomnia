# Release process

## Branch discipline

- 通常開発は専用ブランチから `develop` へ PR を作成する。
- `release/*` ではリリース阻害の修正だけを専用ブランチから受け入れる。
- `release/*` を `main` へマージした後、同じ内容を `develop` へマージバックする。
- タグは `main` に含まれるコミットにだけ作成する。

## Release candidate gate

1. `main` と `develop` の CI が成功していることを確認する。
2. Issue に記録された承認済みの手動試験で、通知、停止、ロック、再ロック、回復を確認する。
3. ログオン、再起動、スリープ復帰、制限終了後の非ロックを確認する。
4. `VERSION` と `CHANGELOG.md` を更新する。
5. repository policy と全自動試験を実行する。
6. canonical builder で単一配布物を生成し、ZIP の構成、SHA-256、個人情報不在を確認する。

```powershell
Invoke-ScriptAnalyzer -Path . -Recurse -Severity Warning,Error
Invoke-Pester -Path .\tests -CI -Output Detailed
dotnet build .\Winsomnia.slnx -c Release
.\scripts\Test-RepositoryPolicy.ps1
.\build-release.ps1
Get-Content .\dist\winsomnia-*-desktop.sha256
```

`build-release.ps1` だけが公開成果物を作成します。出力は `winsomnia-<version>-desktop.zip` と対応する `.sha256` の1組です。ZIP は `Winsomnia.Setup.exe` と隣接する `app` フォルダー内の Desktop/Engine、および利用者向け文書を含み、旧 PowerShell locking runtime や管理 CLI を含みません。

## Tag and GitHub Release

Release candidate は `v0.3.0-rc.1` のような注釈付きタグを使用します。

```powershell
git tag -a v0.3.0-rc.1 -m "winsomnia v0.3.0 release candidate 1"
git push origin v0.3.0-rc.1
```

タグを push すると Release workflow が試験を再実行し、canonical builder の ZIP と SHA-256 だけを添付します。ハイフンを含むバージョンは pre-release として公開されます。既に公開済みの旧 Release asset は置換しません。

## Rollback

安全性へ影響する問題が見つかった場合は Release を pre-release に戻し、利用者へ次を案内します。

1. winsomnia の設定画面で `Pause / 一時停止` を実行する。
2. enable marker が存在しないことと、タスクが無効であることを確認する。
3. インストール先外に展開した既知の安全な Release package から `Winsomnia.Setup.exe uninstall` を実行する。
4. 旧版を再導入する必要がある場合も、先に安全停止を確認し、その版固有の緊急手順を参照する。

```powershell
Test-Path -LiteralPath C:\temp\winsomnia-lock-enabled.json
Disable-ScheduledTask -TaskName winsomnia
.\Winsomnia.Setup.exe uninstall
```

`Test-Path` は `False` である必要があります。実ロック、再起動、marker 作成は通常の rollback 検証に含めません。
