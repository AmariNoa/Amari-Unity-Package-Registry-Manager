# Amari Unity Package Registry Manager

Unity エディターで Scoped Registry と、その接続に使う認証情報を管理するための拡張パッケージです。

## 概要

- Scoped Registry のカタログ（名前・URL・スコープ）を管理します。
- プロジェクトへの Scoped Registry の追加・削除を行います。
- 認証情報を URL ごとに保存し、同じ OS ユーザーのプロジェクト間で共有します。
- 次の認証方式に対応します。
  - npm トークン
  - Basic 認証
  - OAuth Web ログイン（対応する OAuth プロキシが必要です）
  - PAT（Personal Access Token）
  - 認証なし
- UI は日本語と英語に対応しています。

パッケージのインストール自体は、Unity 標準の Package Manager（UPM）が行います。

## 動作環境

- Windows（初期リリースでは Windows のみ対応）
- Unity 2022.3 以降（Unity 2022.3.22f1 で動作確認済み）
- `git` コマンドを PATH から実行できること

## インストール

Unity の Package Manager の「Add package from git URL...」でインストールします。依存パッケージを先にインストールしてください。

1. Unity で `Window > Package Manager` を開きます。
2. 左上の `+` から `Add package from git URL...` を選び、次の URL を入力して追加します。インストールが完了するまで待ちます。

   ```
   https://github.com/AmariNoa/Unity-Editor-Localization-Core.git#v1.0.0
   ```

3. 同じ手順で、次の URL を追加します。

   ```
   https://github.com/AmariNoa/Amari-Unity-Package-Registry-Manager.git#v0.1.0
   ```

補足:

- Unity は `package.json` に書かれた Git パッケージ同士の依存関係を自動で解決できません。そのため、手順 2 の Unity Editor Localization Core を先にインストールする必要があります。
- Unity 公式の Newtonsoft Json（3.2.1）への依存は、UPM が自動で解決します。
- URL 末尾の `#v1.0.0`・`#v0.1.0` はタグ指定です。タグを固定すると、同じバージョンを再現よくインストールできます。
- パッケージはリポジトリのルートにあるため、URL に `?path=` を指定する必要はありません。

## 設定画面の場所

`Edit > Project Settings > Package Manager > Registry Credentials`

`Preferences` や `Tools` メニューではなく、Project Settings の中にあります。

## 基本的な使い方

1. 設定画面でレジストリを選択するか、新しく追加します。名前・URL・スコープを設定します。
2. 必要に応じて、選択したレジストリをプロジェクトに追加、またはプロジェクトから削除します。
3. 認証が必要なレジストリでは、認証方式を選んで認証情報を設定します。
4. パッケージのインストールは、通常どおり Unity の Package Manager で行います。

### 設定 JSON について

レジストリ設定は JSON として書き出し・読み込みができます。書き出す JSON にトークンやパスワードなどの秘密情報は含まれません。ただし、JSON に含まれる URL やスコープなどの内容は、共有先に応じて扱いに注意してください。

## ライセンス

MIT License
