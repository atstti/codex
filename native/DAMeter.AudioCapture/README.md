# DAMeter.AudioCapture

macOS 13 以降の ScreenCaptureKit でシステム音声を取得する、DAMeter 専用の Swift ヘルパーです。マイクは使用せず、音声をファイル保存・外部送信しません。

ScreenCaptureKit の音声出力だけを登録します。macOS 13 の API には画面フィルターが必要ですが、画面の出力ハンドラーは登録せず、画面画像はアプリへ渡しません。画面／システム音声の収録権限が必要です。保護されたコンテンツなど、macOS が取得を許可しない音声は取得できません。

## ビルド

macOS 上で Xcode Command Line Tools と macOS SDK が必要です。

```bash
xcrun swiftc -parse-as-library -swift-version 5 -O \
  -target arm64-apple-macos13.0 \
  -framework ScreenCaptureKit -framework CoreMedia -framework CoreGraphics \
  -framework AudioToolbox -framework AppKit \
  native/DAMeter.AudioCapture/main.swift -o DAMeter.AudioCapture
./DAMeter.AudioCapture --check
./DAMeter.AudioCapture --self-test
```

Intel 用は `-target x86_64-apple-macos13.0` に変更します。`--check` は権限要求も音声取得も行いません。`--self-test` は実際の PCM 変換・パケット生成処理に既知のバッファを渡し、ステレオ、平面配置、モノラル、PCM 8/16/24bit、上位詰め24bit、Float 32/64bit、Big Endian、非有限値、短いバッファ拒否を 9 ケースで検証します。こちらも権限・実デバイスは不要です。コンパイルとこれらの成功は、実音声取得の動作確認を意味しません。

実際のキャプチャでは、配布スクリプトで組み立てた `DAMeter.app/Contents/Helpers/DAMeter.AudioCapture.app/Contents/MacOS/DAMeter.AudioCapture` を使用します。ヘルパーは親アプリとは別のbundle ID `org.atstti.DAMeter.AudioCapture` と実行ファイル名を持つ `LSUIElement` エージェントです。通常アプリとしてDockへ追加せず、音声取得に必要なOSの権限ダイアログは引き続き表示します。配置・識別子・エージェント設定が不正な場合はキャプチャ前に終了します。

`--agent-check` はこのbundle ID、実行ファイル名、`LSUIElement=true`、AppKitのaccessory activation policyを検証します。権限要求や音声取得は行わず、配布CIで両CPU向けに実行します。単体でコンパイルした実行ファイルにはエージェントbundle情報がないため、`--check` と `--self-test` のみ使用できます。

## プロセス間のプロトコル

- stdout: バイナリのみ。`UInt32 little-endian` の payload 長（バイト）に続けて、ステレオの interleaved `Float32 little-endian` データを出力します。payload は 8 の倍数、上限 4 MiB です。
- IEEE Float 32/64bit、整数 PCM の平面／インターリーブ配置を安全に読みます。モノラルは両側へ複製、マルチチャンネルは先頭 2ch、非有限サンプルは無音として扱います。
- stderr: `STATUS:` または `ERROR:` に続く UTF-8 の状態・例外テキストです。音声サンプルをログに含めません。
- stdin: `stop` と改行、または EOF で終了します。SIGTERM/SIGINT と親プロセス終了も監視します。
- 終了コード: `0` 通常終了／チェック・自己テスト成功、`64` 引数不正、`69` 未対応OS、`70` キャプチャ／接続エラー、`77` 権限未許可、`78` エージェントの配置・設定不正。77は許可の変更、78は正しい配布版の再配置後に、ユーザー操作による再試行を待ってください。

停止時は stream を停止し、音声出力を外して解放します。表示や音声経路が変わって stream が停止した場合は非ゼロで終了し、親アプリが再接続を管理します。

API パターンは Apple の「Capturing screen content in macOS」サンプルにある `SCStreamOutput` と `CMSampleBuffer.withAudioBufferList` を参照しています。製品コードはこのリポジトリ独自の実装です。
