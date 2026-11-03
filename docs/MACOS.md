# DAMeter Mac版

## 0.1.1：起動・終了の修正

音声取得プロセスを専用のバックグラウンドアプリとして分離し、メインのDAMeterと同じアプリとして扱われない構成に変更しました。macOSの「終了」やDockからの終了、⌘Qをウィンドウ非表示としてキャンセルしてしまう不具合も修正しています。通常画面には「終了」ボタンを追加し、ヘルパーの停止・読み取り待ちには時間制限を設定しました。

更新前に旧版のDAMeterを終了してください。操作できない場合は⌘＋⌥＋EscでDAMeterを強制終了します。複数表示されている場合は両方終了してから、新しいDMGのDAMeter.appで「アプリケーション」内の旧版を置き換えてください。

音声取得の許可に「DAMeter 音声取得」と表示される場合があります。これはDockに通常アプリを追加しない専用ヘルパーです。許可するかどうかはmacOSのダイアログで選択でき、拒否した場合は自動で起動を繰り返しません。

macOS のシステム音声を表示する Avalonia / .NET 10 アプリです。Windows版の WPF / WASAPI 実装を維持し、音声解析、ピーク・RMS計算、表示応答、JSON設定を共有しています。

## 動作対象

- macOS 15 以降の、使用する .NET 10 ランタイムがサポートするバージョン
- Apple Silicon: `osx-arm64`（M1 / M2 / M3 / M4 など）
- Intel Mac: `osx-x64`

音声取得には macOS 標準の ScreenCaptureKit を使用します。ScreenCaptureKit の音声APIは macOS 13 以降で利用できますが、本アプリの対応OSは .NET 10 のサポート範囲に合わせています。自己完結型なので、配布アプリの実行時に .NET SDK を別途インストールする必要はありません。

## ダウンロードと起動

1. GitHub のリポジトリで **Actions → Build macOS** を開き、成功した実行を選びます。
2. **Artifacts** から、自分のCPUに合った `DAMeter-osx-arm64` または `DAMeter-osx-x64` をダウンロードします。
3. Artifacts のZIPを展開します。中にアプリ入りZIP、DMG、チェックサム、検証記録があります。
4. DMGを開いて `DAMeter.app` を「アプリケーション」へドラッグするか、アプリ入りZIPを展開してアプリを移動します。
5. `DAMeter.app` を起動します。

初回の両CPU対応ビルドは [こちらの成功した実行](https://github.com/atstti/codex/actions/runs/41603225132) から取得できます。Swiftコンパイル、C#テスト、署名検証、音声変換9件、権限不要の3表示起動チェックとZIP/DMG生成を確認しています。ユーザーの音声出力や権限ダイアログを使う実音声テストは別途必要です。

「このMacについて」で「チップ: Apple M…」と表示される場合は Apple Silicon、「プロセッサ: Intel…」と表示される場合は Intel です。

この開発ビルドは **ad-hoc署名で、Developer ID署名・Apple公証はありません**。初回起動がmacOSにブロックされた場合は、内容と配布元を確認したうえで「システム設定 → プライバシーとセキュリティ」の「このまま開く」を使用してください。Gatekeeper全体を無効化する必要はありません。

## システム音声へのアクセス許可

ScreenCaptureKit では、システム音声の取得に「画面収録」または「画面とシステムオーディオの収録」に関する許可が必要です。表示名はmacOSのバージョンによって異なります。初回起動時のダイアログでDAMeterを許可し、必要に応じてアプリを終了して再起動してください。設定画面でDAMeterまたは同梱の音声ヘルパーへのアクセスを確認できます。

アプリは画面フレームの受信先を登録せず、音声のみを処理します。マイクは取得しません。音声バッファはネイティブヘルパーとC#アプリ間のパイプを通って解析され、処理後に破棄されます。音声・画面のファイル保存、録画、外部送信は行いません。権限を拒否した場合は状態表示を確認し、システム設定で許可を変更してから再試行してください。

## 表示とWindows版との差分

- 横型ピーク、縦型ピーク、アナログVUの3種類を切り替えられます。
- ピーク・RMS・dBFS、ホールド、減衰、クリップ判定はWindows版と同じ共通処理を使用します。
- VUは約300msのRMSエネルギー応答による近似表示であり、厳密な規格準拠ではありません。
- フローティング表示、背景不透明度、最前面、クリック透過、メニューバーからの復帰をMac向けに実装しています。
- ScreenCaptureKitでシステム音声を取得する方式のため、Windows版のWASAPI出力エンドポイント選択はありません。Macのシステムサウンド設定で出力先を選びます。
- DRMなどで保護されたコンテンツ、ヘッドレス環境、アプリやOS側の取得制限によって、音声を取得できない場合があります。

クリック透過で操作できなくなった場合は、メニューバーのDAMeterアイコンから表示・通常表示への復帰を選びます。音声の反応がない場合は、権限、システム音声の再生、状態表示を確認してください。

## Macでの開発と配布ビルド

前提は `global.json` に指定した .NET 10 SDK と、Xcodeコマンドラインツールです。リポジトリのルートで実行します。

```bash
# C#の復元・ビルド・共通テスト
dotnet restore src/DAMeter.Mac/DAMeter.Mac.csproj --locked-mode
dotnet build src/DAMeter.Mac/DAMeter.Mac.csproj --configuration Release --no-restore --warnaserror
dotnet restore tests/DAMeter.Core.Tests/DAMeter.Core.Tests.csproj --locked-mode
dotnet test tests/DAMeter.Core.Tests/DAMeter.Core.Tests.csproj --configuration Release --no-restore

# Apple Silicon向けの自己完結アプリ・ZIP・DMG
bash scripts/publish-macos.sh osx-arm64

# Intel向け
bash scripts/publish-macos.sh osx-x64
```

生成先はそれぞれ `artifacts/macos-osx-arm64/`、`artifacts/macos-osx-x64/` です。`DAMeter.app/Contents/MacOS/` に自己完結型の単一 .NET 実行ファイルとSwift音声ヘルパーを配置します。macOSの署名規則に合わせてマネージDLLをこのコード用ディレクトリへ直接配置せず、.NETの単一ファイル機能で組み込みます。ネイティブライブラリは実行時にユーザーの一時領域へ展開されます。ヘルパーを使うため、開発中も配布スクリプトで組み立てた `.app` を起動してください。

スクリプトは Swift のコンパイル、ad-hoc署名、署名の整合性検証、権限不要のヘルパー `--check` とPCM変換9件の `--self-test`、Avaloniaの実ウィンドウで3表示を描画して閉じる `--smoke-test`、ZIP・DMG・SHA-256生成を行います。スモークテストでは音声取得・トレイ・設定保存を開始しません。異なるCPU向けへのクロス発行時は実行確認を省略します。GitHub Actions はCPUに一致するmacOSランナーでそれぞれビルドします。

通常のC#ビルドと、CPUごとの自己完結発行は異なるNuGetロックファイルを使います。依存関係変更時は対応するロックファイルも更新してください。

## 検証範囲

Linuxで検証できるのは、C#のビルド、共通数値処理、設定保存、ヘルパーパイププロトコルなどです。Swift / macOSフレームワークのコンパイルと署名・パッケージ作成はmacOSで行います。

CIのビルドや権限不要チェックに成功しても、ユーザーのMacでの実音声、権限ダイアログ、メーター描画、透過からの復帰が確認されたことにはなりません。以下は実機での確認が必要です。

- 初回アクセス許可、拒否した状態、許可変更後の再試行
- ブラウザ・音楽プレーヤーなどの実音声、無音、L/R独立信号
- 3表示、VU基準レベル、ホールド、減衰、クリップ
- フローティングの移動・サイズ変更、背景0/50/100%、最前面
- クリック透過中のメニューバーからの復帰、終了時のヘルパー停止
- 再起動時の設定復元、複数モニター、Retina、高DPI、モニター切断
- 長時間動作時のCPU・メモリ・ハンドル数
