# 配信スタートランチャー (StreamLauncher)

配信で使うアプリをまとめて起動・終了する Windows 用ツールです。

- 好きなアプリを起動リストに追加して、上から順に起動
- 起動後に押したいボタンを自動でクリック(UI Automation)
  - 例: CastCraft の「Screen」→ 確認ダイアログの「起動する」
- 「全部終了」で、起動したのと逆の順番にまとめて終了
  - OBS など強制終了すると困るアプリは、強制終了の前に確認
- OBS / CastCraft / VSeeFace のプリセット入り(初回に自動で場所を探します)
- 自動アップデート(起動時に確認、ボタン1つで更新。更新ファイルは署名検証済みのものだけ適用)

## ダウンロード

[Releases](../../releases/latest) から `StreamLauncher-vX.Y.Z.zip` をダウンロードして展開し、
`StreamLauncher.exe` を起動してください。

- 動作環境: Windows 10 / 11(.NET Framework 4.8。追加インストール不要)
- 初回に「WindowsによってPCが保護されました」と出た場合は「詳細情報」→「実行」
- 設定は `%APPDATA%\StreamLauncher\settings.json` に保存されます

使い方の詳細は [README.txt](README.txt) を見てください。

## ビルド

```
dotnet build src/StreamLauncher.csproj -c Release
```

`dist/` に `StreamLauncher.exe` が出力されます。
`v*` のタグを push すると GitHub Actions がビルドして Releases に zip を公開します。

### 自動アップデートの仕組み

- Release には `StreamLauncher.exe` と `latest.json`(バージョン・SHA-256・RSA 署名)が付きます。
- アプリは `releases/latest/download/latest.json` を確認し、`src/Updater.cs` に埋め込んだ公開鍵で
  署名を検証できた exe だけを適用します。
- 署名用の秘密鍵はリポジトリに含めず、GitHub Actions の Secret `UPDATE_SIGNING_KEY` に置きます
  (RSA 秘密鍵の XML を base64 にしたもの)。
- アイコンは `tools/make-icon.ps1` で生成しています。

## ライセンス

[MIT License](LICENSE)
