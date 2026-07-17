# Release process

## Branch discipline

- 通常開発は専用ブランチから`develop`へPRを作成する。
- `release/*`ではリリース阻害の修正だけを専用ブランチから受け入れる。
- `release/*`を`main`へマージした後、同じ内容を`develop`へマージバックする。
- タグは`main`に含まれるコミットにだけ作成する。

## Release candidate gate

1. `main` と `develop` のCIが成功していることを確認する。
2. キルスイッチを用意した制御試験で、ロック・再ロック・自動復旧を確認する。
3. ログオン、再起動、スリープ復帰、06:00以降の非ロックを確認する。
4. `VERSION` と `CHANGELOG.md` を更新する。
5. 配布物を生成し、ZIPの内容とSHA-256を確認する。
6. リポジトリポリシー検査が成功し、ZIPに緊急復旧手順が含まれることを確認する。

```powershell
.\build-release.ps1
.\build-desktop.ps1
.\scripts\Test-RepositoryPolicy.ps1
Get-Content .\dist\winsomnia-*.sha256
```
正式版の利用者には、`dist\winsomnia-<version>-desktop.zip`を配布します。このZIPは`Winsomnia.Setup.exe`と、その隣にある`app`フォルダーを含むため、展開後のフォルダー構成を保ったままセットアップを実行します。導入後は[docs/INSTALL.md](docs/INSTALL.md)の安全確認とドライランを完了してから再開します。

## Tag and GitHub Release

リリース候補は `v0.1.0-rc.1` のような注釈付きタグを使用します。

```powershell
git tag -a v0.1.0-rc.1 -m "winsomnia v0.1.0 release candidate 1"
git push origin v0.1.0-rc.1
```

タグをpushするとReleaseワークフローがテストを再実行し、ZIPとSHA-256を添付したGitHub Releaseを作成します。ハイフンを含むバージョンはpre-releaseとして公開されます。

正式版では `VERSION` とCHANGELOGを `0.1.0` に更新し、同じ手順で `v0.1.0` を作成します。

## Rollback

配布後に安全性へ影響する問題が見つかった場合は、GitHub Releaseをpre-releaseへ戻し、利用者へ次を案内します。

```powershell
.\winsomnia.ps1 pause
.\winsomnia.ps1 uninstall
```
