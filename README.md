# Tasklabe

日本語 | [English](README.en.md)

GitHub Projects をデータの保存先にした、Windows 向けのタスク・プロジェクト管理アプリ

**No Electron。No WebView。100% ネイティブ。**

通信先は GitHub のみ。利用状況などの情報は一切収集しません。

> [!NOTE]
> 現在ベータ版です。今後のアップデートで、画面や機能が変わる場合があります。

## 主な機能

- **マイタスク**: 個人のタスクと、チームで自分に割り当てられたタスクをまとめて確認
- **計画**: 階層構造でタスクを整理し、担当者・日程・工数・進捗率を管理
- **ガントチャート**: 予定と実績の差をひと目で把握。イナズマ線と依存関係の表示に対応
- **カンバン**: ドラッグ＆ドロップでステータスを変更
- **プロジェクトの状況**: 進捗率、予定との差、遅れているタスク、担当者ごとの負荷を確認
- **キーボード操作**: 主な操作はショートカットキーで実行可能。コマンドパレットにも対応

タスクは GitHub の Issue、プロジェクトは GitHub Project として保存されるため、GitHub の画面からも同じデータを確認・編集できる。

## 動作環境

- Windows 10 バージョン 2004（ビルド 19041）以降、または Windows 11
- x64 または ARM64
- GitHub アカウント（github.com）

※ Japanese Only

## インストール

[Releases](https://github.com/fkawabata/Tasklabe/releases) から zip ファイルをダウンロードして展開し、`Tasklabe.exe` を起動

> [!IMPORTANT]
> ベータ版はコード署名をしていないため、初回起動時に「Windows によって PC が保護されました」と表示されることがあります。その場合は「詳細情報」をクリックし、「実行」をクリックしてください。

## 使い方

### 権限

Tasklabe は、次の権限を使用します。アクセストークンは、Windows の資格情報マネージャーに保存されます。

| 権限 | 用途 |
|---|---|
| `repo` | Issue（タスク）の読み書き、個人用リポジトリの作成 |
| `project` | GitHub Project の作成と読み書き |
| `read:org` | 所属している Organization の取得 |

### 初回セットアップ

初めてサインインしたときに、次のものが自動で作成されます。

- 個人のタスク用の private リポジトリ（既定の名前: `tasklabe-personal`。既存のリポジトリを指定することもできます）
- 個人のタスク用の GitHub Project（アプリでは「マイタスク」として表示されます）

### チームで使う

チームのプロジェクトは、Organization の GitHub Project として作成します。メンバーには、その Project とタスクを置くリポジトリの両方に、書き込み権限を付与してください。

サインイン時の承認画面では、Organization ごとの許可も忘れないでください。Organization で OAuth App のアクセス制限が有効な場合は、Owner による Tasklabe の承認が必要です。

## アンインストール

アプリの設定画面からサインアウトし、展開したフォルダーと `%LOCALAPPDATA%\Tasklabe` を削除します。GitHub 上のデータは削除されません。

## ビルド

Require: .NET 10 SDK

```powershell
dotnet build Tasklabe.slnx -c Release
dotnet run --project src/Tasklabe.App -c Release
```

## ライセンス

[MIT License](LICENSE)
