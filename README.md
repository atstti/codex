# DAMeter

**Mac版を追加しました。** macOS 15以降の Apple Silicon / Intel 向けに、Avalonia と ScreenCaptureKit でシステム音声を取得します。Mac版の導入・権限・配布手順は [docs/MACOS.md](docs/MACOS.md) を参照してください。Windows版のWPF / WASAPIは維持しています。両版は音声解析と設定保存を共有します。

Windows 10 / 11（64bit）で再生中のシステム音声を表示する、C# / .NET 10 / WPF のデスクトップ音声メーターです。NAudio の WASAPI Loopback で再生デバイスを取得します。マイク入力やデモ信号は使用しません。

音声バッファはメモリ内で計算後に破棄します。音声の録音、ファイル保存、外部送信は行いません。保存するのは表示設定と例外ログです。

## 実装機能

| 項目 | 内容 |
| --- | --- |
| 音声取得 | 既定の再生デバイスへの自動追従、出力デバイスの手動選択、未接続時の既定出力へのフォールバックと再試行 |
| 横型ピーク | L/R 独立の LED バー、緑・黄・赤、-60～0 dBFS の目盛り、数値、ピークホールド、クリップ表示 |
| 縦型ピーク | コンパクト表示に対応した L/R バー、dBFS 目盛り、ピークホールド、クリップ表示 |
| アナログ VU | アイボリーの左右盤面、黒い目盛り、赤い針、RMS ベースの滑らかな応答、基準レベル調整 |
| 通常表示 | ダークテーマ、ワンクリックで 3 表示切り替え、デバイス選択、最前面、設定画面 |
| フローティング | タイトル・枠なし、背景不透明度 0～100%、メーター部分の独立した視認性、ドラッグ移動、右下グリップでサイズ変更 |
| 操作の復帰 | 右クリックメニュー、クリック透過 ON/OFF、通知領域からの表示・非表示・通常表示への復帰 |
| 設定・安定性 | JSON 保存、モードごとの位置とサイズ、破損設定の初期値復旧、例外ログ、Per-Monitor DPI 対応宣言、モニター構成変更時の位置補正 |

これらはソースに実装した機能です。Linux 上では音声の数値処理と設定保存を検証しています。Windows での音声取得、UI、デバイス切断、高 DPI、CPU 使用率は未検証です。

## 起動と使い方

開発には `global.json` の .NET SDK **10.0.403** を使用します。同じ feature band の新しいパッチも許可します。WPF を起動するには Windows 10 / 11 x64 が必要です。Visual Studio を使う場合は .NET 10 と WPF をサポートする版で `DAMeter.slnx` を開いてください。

リポジトリのルートで実行します。

```powershell
dotnet restore DAMeter.slnx
dotnet build DAMeter.slnx --configuration Release --no-restore --warnaserror
dotnet test tests/DAMeter.Core.Tests/DAMeter.Core.Tests.csproj --configuration Release --no-restore
dotnet run --project src/DAMeter/DAMeter.csproj --configuration Release
```

1. 音楽や動画などを再生すると、選択した再生デバイスの L/R レベルを表示します。
2. 上部の「横型ピーク」「縦型ピーク」「アナログ VU」で表示を切り替えます。
3. 「既定の再生デバイス（自動追従）」が初期設定です。手動選択する場合は一覧で出力を選び、追加した機器が見えない場合は「更新」を押します。
4. 「設定」で背景不透明度、ピークホールド時間、減衰速度、VU 基準レベルを変更します。
5. 「浮動表示」でメーター主体の表示に切り替えます。メーターを左ドラッグすると移動し、右下のグリップでサイズ変更できます。右クリックメニューでも表示や最前面を変更できます。

### クリック透過からの復帰と終了

クリック透過はフローティング中に有効にできます。透過中はウィンドウへのクリックを下のアプリへ通すため、右クリックもできなくなります。

- 通知領域の DAMeter アイコンをダブルクリック、または「ウィンドウを表示（クリック透過を解除）」を選ぶと透過を解除して表示します。
- トレイの「通常モードに戻す」でも操作可能な通常表示へ戻れます。
- アイコンが見えない場合は Windows の通知領域の隠れたアイコン一覧を開いてください。
- 通常画面の **×** やウィンドウを閉じる操作は、通知領域へ隠す動作です。音声計測は継続します。終了はトレイまたは右クリックメニューの「アプリ終了」「終了」を使用してください。

トレイアイコンを用意してから、保存されたクリック透過設定を復元する構成です。

## 計測方法

- PCM 8/16/24/32bit と IEEE Float 32/64bit を扱います。WAVEFORMATEXTENSIBLE の PCM / Float サブ形式も識別します。
- モノラルは両方のメーターへ複製します。マルチチャンネルは最初の 2 チャンネルのみを L/R として計測し、他チャンネルをミックスしません。
- チャンネルごとのサンプル絶対値の最大値と RMS を求めます。複数バッファの RMS はサンプルフレーム数で重み付けします。
- `dBFS = 20 × log10(amplitude)`。無音は内部で負の無限大とし、ピーク表示範囲の下限は -60 dBFS です。
- 高速アタックと時間ベースの dB 減衰、ピークホールドを使用します。振幅 1 以上で約 1 秒間のクリップ表示を行います。
- VU は RMS エネルギーの約 300ms の指数応答と調整可能な基準レベルを使う **VU 風の近似表示** です。厳密な規格準拠 VU メーターではありません。
- 音声処理と描画を分離し、描画は約 60fps を目標とするタイマーで更新します。実際の更新速度は負荷に依存し、保証値ではありません。
- WASAPI のバッファ到着間隔を橋渡しするため、最後の計測値を最大 150ms 保持します。その後に新規バッファがなければ無音として処理します。
- デバイス状態を約 2 秒ごとに再確認します。手動選択したデバイスが消えた場合は既定出力へ一時退避し、再接続を試みます。

## 設定とログ

Windows の `%LOCALAPPDATA%\DAMeter` に次を保存します。

- `settings.json`: メーター種別、デバイス ID、通常／浮動の位置とサイズ、浮動状態、背景不透明度、最前面、クリック透過、ピークホールド、減衰速度、VU 基準レベル。
- `exceptions.log`: 例外の時刻と詳細。約 2 MiB を超えると `exceptions.previous.log` に世代交代します。

設定は一時ファイルへ書いてから置き換えます。設定の破損や読み取り失敗はログに記録し、初期値で起動します。設定値は有限値と許容範囲を確認します。モニターの左側や上側で使う負の座標は保持します。

## Windows x64 向けの配布ビルド

実行先に .NET を別途導入せず起動する、自己完結型の単一 exe を生成するコマンドです。

```powershell
dotnet publish src/DAMeter/DAMeter.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output artifacts/win-x64
```

生成先は `artifacts/win-x64/DAMeter.exe` です。ビルド時には Windows ランタイムなどの取得にネットワーク接続が必要です。ネイティブファイルの一部は初回起動時にユーザーの一時領域へ展開されます。配布前に Windows で起動と実音声の受け入れ確認を行ってください。コード署名とインストーラーは含みません。

通常の依存関係は `packages.lock.json`、単一 exe 発行時に追加される SDK タスクと runtime 固有の依存関係は `packages.publish.lock.json` に固定しています。通常の復元には `--locked-mode`、上記の発行には `-p:RestoreLockedMode=true` を追加して固定内容を検証できます。

Windows の PowerShell では `./scripts/publish.ps1` で、圧縮した exe、README、検証記録、依存ソフトウェアのライセンス、SHA-256 をまとめた `artifacts/DAMeter-win-x64.zip` を作成できます。Windows CIでこのPowerShellスクリプトとArtifacts生成の成功を確認しました。Linuxからのクロス発行にも成功しています。

## Linux クラウド環境

`EnableWindowsTargeting` を有効にしているため、Linux でも Windows 向け参照アセンブリを復元してビルドできます。WPF、WASAPI、通知領域の実行検証はできません。

このクラウド環境では SDK を `/workspace/.dotnet` に配置します。シェルごとに設定します。

SDK の導入とビルド・テストの再実行には `bash scripts/setup-cloud.sh` を使用できます。公式 SDK の SHA-512 を検証して導入し、既存 SDK が一致する場合は再利用します。

```bash
export DOTNET_ROOT=/workspace/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME=/workspace/.dotnet-home
export NUGET_PACKAGES=/workspace/.nuget/packages
cd /workspace/codex
dotnet restore DAMeter.slnx
dotnet build DAMeter.slnx --configuration Release --no-restore --warnaserror
dotnet test tests/DAMeter.Core.Tests/DAMeter.Core.Tests.csproj --configuration Release --no-restore
```

同じ環境で上記の `dotnet publish` コマンドも実行できます。既存チェックアウトを使い、追加の Git worktree は不要です。

## 構成とテスト

| 場所 | 役割 |
| --- | --- |
| `DAMeter.slnx` | Visual Studio / .NET ソリューション |
| `src/DAMeter.Core` | プラットフォーム非依存のサンプル解析、表示応答、設定とログ |
| `src/DAMeter/Audio` | NAudio WASAPI Loopback、再接続、スレッド間の計測値受け渡し |
| `src/DAMeter/ViewModels` | MVVM の状態・コマンド、音声取得と UI 更新の連携 |
| `src/DAMeter/Controls` | 3 種類のカスタム描画 |
| `src/DAMeter/Platform` | トレイ、クリック透過、モニターとウィンドウ位置 |
| `tests/DAMeter.Core.Tests` | xUnit による数値処理と設定保存の検証 |

Linux / .NET SDK 10.0.403 で **Core 46件・Mac UI/通信11件、計57件成功、失敗0・スキップ0** を確認しました。Windowsと両Mac CPUのCIビルド・配布物生成、Macの権限不要の音声変換セルフテストと起動チェックも成功しています。実音声と手動操作の受け入れ確認は未実施です。詳細と受け入れチェックリストは [docs/TESTING.md](docs/TESTING.md) を参照してください。
