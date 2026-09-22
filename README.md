# LocalMediaKeys

リモートデスクトップ (mstsc) を全画面で使っていても、マウスやキーボードのメディアキー（次の曲 / 前の曲 / 再生・一時停止 / 停止 / 音量アップ / 音量ダウン / ミュート）を**ローカル側**で処理する常駐ツールです。

Keep media and volume keys working on the *local* PC while a Remote Desktop (mstsc) session is fullscreen. The RDP client normally forwards every keystroke, media keys included, to the remote machine, so a mouse button mapped to "next track" skips a song on the remote PC instead of the one playing locally. LocalMediaKeys intercepts those keys with a low-level keyboard hook and routes them to the local shell instead.

- 追加インストール不要。Windows 同梱の .NET Framework だけで動く単体 exe です。
- 管理者権限は不要です。
- 外部ライブラリなし。

## このアプリが解決する問題

マウスソフトで「次の曲」をボタンに割り当てると、それはキーボードのメディアキー（`VK_MEDIA_NEXT_TRACK`）として送られます。リモートデスクトップが前面にあると mstsc がそのキーをリモートへ転送するため、ローカルで再生中の曲はスキップされません。

## 使い方

1. `LocalMediaKeys.exe` を起動する。通知領域に青い ▶▶| アイコンが出ます。
2. リモートデスクトップを全画面にして、マウスの「次の曲」ボタンや音量ボタンを押す。ローカルのプレーヤーに届きます。

## トレイアイコンの右クリックメニュー

| 項目 | 内容 |
|---|---|
| 有効 | チェックを外すと何もしない。アイコンのダブルクリックでも切り替え |
| リモートデスクトップが前面のときだけ横取り | 既定でオン。オフにすると常にローカル処理 |
| Windows 起動時に自動起動 | `HKCU` の Run キーに登録 / 解除 |
| 終了 | |

## 対応キー

| キー | 動作 |
|---|---|
| 次の曲 / 前の曲 / 再生・一時停止 / 停止 | 押し続けても1回だけ |
| 音量アップ / 音量ダウン | 押し続けると連続で変化 |
| ミュート | 押し続けても1回だけ |

## 仕組み

低レベルキーボードフック (`WH_KEYBOARD_LL`) でメディアキーを mstsc より先に受け取り、リモートへ転送される前に握りつぶし、代わりにローカルのタスクバー (`Shell_TrayWnd`) へ `WM_APPCOMMAND` を投げます。キーボードのメディアキーを押したときと同じ経路で、Windows が「現在の再生中」と認識しているプレーヤーに届きます。

後から入れたフックほど先に呼ばれるため、RDP クライアントが前面に来るたびにフックを掛け直し、mstsc 自身のフックより先に受け取れるようにしています。mstsc に加えて Windows App (`msrdc`) も RDP クライアントとして認識します。

## うまく動かないとき

- RDP 接続後に本ツールを起動し直してみてください（フックの順序の問題）。通常は自動で掛け直すので不要です。
- 通常のキーボードのメディアキーでローカルの曲送りができない環境では、本ツールでも送れません。プレーヤー側がメディアキーに対応している必要があります。
- 診断ログが `%LOCALAPPDATA%\LocalMediaKeys\LocalMediaKeys.log` に残ります（起動ごとに作り直し）。メディア/音量キーの受信と処理結果、RDP 前面判定の切り替わりを記録します。

## ビルド

```bat
build.cmd
```

.NET Framework 同梱の `csc.exe` を使うので、Visual Studio や .NET SDK は要りません。設定は `HKCU\Software\LocalMediaKeys` に保存されます。

## ライセンス

MIT License. See [LICENSE](LICENSE).
