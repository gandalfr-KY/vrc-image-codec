# VRC Image Codec

Unity / VRChatワールド内で、RenderTextureをAVIF・JPEGに圧縮し、圧縮した画像をRenderTextureへ復元するUdonSharpライブラリです。GPUで画像処理を行い、エンコードとデコードを対になるAPIで提供します。

```text
RenderTexture → ImageEncoder → byte[] → ImageDecoder → RenderTexture
```

撮影、表示、通信、暗号化、URL生成、ファイル保存は利用側のプロジェクトが担当します。このライブラリを使うためのサーバーやネイティブプラグインは不要です。

## 動作環境と対応範囲

- Unity 2022.3 / VRChat Worlds SDK 3.10.5以降（UdonSharp・ClientSimを含む）。
- PC向けShader Model 5.0。Direct3D 11で検証しています。Quest / モバイルは非対応です。他のGraphics APIは未検証です。
- AVIF・JPEGともに、8-bit SDR、4:2:0または4:4:4の静止画を扱います。
- 出力は各辺16〜1920ピクセル、8の倍数、合計2,073,600ピクセル以下です。縦長も使えます。
- **デコーダーは、このライブラリが生成する形式に対応します。外部ソフトで作った任意のAVIF・JPEGを読めるものではありません。** アニメーション、HDR、アルファ、Progressive JPEGなどは非対応です。

生成したファイルは、対応する一般的な画像ビューアーで開けます。デコーダーが受け付ける詳細なプロファイルは[実装資料](Documentation/Implementation.md)に記載しています。

## 導入する

既存のVRChat Worlds SDKプロジェクトに、[Releases](https://github.com/gandalfr-KY/vrc-image-codec/releases/latest) から本体の `.unitypackage` を取得してインポートしてください。パッケージとして管理する場合は、zipの内容を `Packages/com.gandalfr.vrc-avif` に配置できます。既存プロジェクトではパッケージ管理方法を混在させないでください。

1. **Tools > Image Codec > Create encoder** を選びます。
2. Inspectorで入力RenderTextureと圧縮設定を指定します。
3. 復元も行う場合は、**Tools > Image Codec > Create decoder** を選びます。

このメニューから作成すると、内部Component、Material、作業用Texture、Udonプログラムが用意されます。通常は、空のGameObjectにComponentだけを追加して内部設定を手作業で埋める必要はありません。Inspectorの表示は英語です。

### Encoderの設定

設定はPlayモードに入る前に行います。

| 項目 | 指定する内容 |
| --- | --- |
| Format | AVIFまたはJPEG |
| Chroma sampling | 4:2:0は色の情報を減らして容量を節約します。4:4:4は色の細部を保ちます |
| Input RenderTexture | 圧縮したい画像が入るRenderTexture |
| Resolution | 入力の縦横比を保つ出力サイズ |
| Quality | 1〜100。初期値は60。大きいほど通常は高画質・大容量になります |

入力の幅・高さも、それぞれ16以上、8の倍数にしてください。Resolutionには、入力を超えず、縦横比と対応上限を満たす候補だけが表示されます。引き伸ばしや切り抜きは行いません。候補が一つしか出ない場合は、より大きく、16:9・4:3・1:1などの比率を持つ入力を用意すると選びやすくなります。

Inspectorには処理時間とバイト長の大まかな目安も表示します。画像内容・GPU・フレームレートで変わるため、上限や性能保証ではありません。AVIFとJPEGのQualityは、同じ数値でも同じ画質を意味しません。

Decoderには圧縮方式・解像度・サンプリングの設定はありません。渡されたバイナリから読み取ります。

## まずサンプルを動かす

unitypackageで導入した場合は、対応する `-sample.unitypackage` もインポートしてください。パッケージとして導入した場合は、Package ManagerのSamplesから **Encode, decode and display** をインポートします。手動で `Samples~/RoundTrip` をAssetsへコピーしても使えます。

1. Projectで16:9の画像を選びます。省略するとテスト用の模様を生成します。
2. **Tools > Image Codec > Create round-trip sample** を選びます。
3. Playモードに入り、**Encode & Decode** を押します。

左が入力、右が圧縮後に復元した画像です。完了すると圧縮済みバイナリのバイト数が表示されます。設定を変えるときはPlayモードを止め、子の **Image encoder** を選びます。

サンプルの `ImageCodecLink` は、Encoder・Decoder・表示先RawImageをInspectorでつなぐ例です。UIへの表示はサンプル側にあり、コーデック本体はUnity UIに依存しません。独自のカメラ・通信システムでは、以下の公開APIから直接受け渡せます。

## UdonSharpから使う

利用するクラスは `VrcImageCodec.ImageEncoder` と `VrcImageCodec.ImageDecoder` です。Inspectorで作成したComponentを、自分のUdonSharpスクリプトの参照欄へ割り当てます。以下のAPIは、通常のMonoBehaviourからプロキシを直接操作するのではなく、UdonSharpスクリプトから呼び出します。

### 圧縮を開始する

```csharp
// encoderは、Inspectorで設定したImageEncoder。
// InputTextureには、圧縮したい画像を先に描画しておきます。
bool accepted = encoder.TryBeginEncoding(encoder.InputTexture);
```

`true` は「開始を受け付けた」という意味です。処理は複数フレームにまたがるため、その場では結果を受け取れません。自分のUpdateなどで完了を待ちます。

```csharp
if (!encoder.IsBusy)
{
    if (encoder.IsComplete)
    {
        byte[] bytes = encoder.ResultBytes;
        // 通信・保存・デコードなど、自分の処理へ渡します。
    }
    else
    {
        Debug.Log(encoder.LastError);
    }
}
```

この確認は、開始を受け付けた処理について一度だけ結果を扱うようにしてください。`IsComplete` は次の開始・キャンセルまでtrueのままなので、毎フレーム送信すると同じ結果を繰り返し送ってしまいます。サンプルではこの待機状態を管理しています。

引数には、設定済み入力とは別のRenderTextureも渡せます。入力のサイズ条件と出力との縦横比を満たす必要があります。出力サイズ・形式はComponentに事前設定した値を使います。開始時に内部Textureへ画像をコピーするので、開始後も外部の入力RenderTextureを描画に使えます。

### 復元を開始する

```csharp
bool accepted = decoder.TryBeginDecoding(bytes);
```

開始後、`decoder.IsBusy` がfalseになったら `decoder.IsComplete` を確認します。成功時の画像は `decoder.ResultTexture`、失敗理由は `decoder.LastError` から取得できます。RenderTextureをRawImageやMaterialへ割り当てる処理は、利用側で行います。サンプルにはRawImageへ表示する例があります。

### API一覧

| 操作・情報 | Encoder | Decoder |
| --- | --- | --- |
| 開始を試みる | `TryBeginEncoding(RenderTexture)` | `TryBeginDecoding(byte[])` |
| キャンセル | `CancelEncoding()` | `CancelDecoding()` |
| 処理中か | `IsBusy` | `IsBusy` |
| 成功して完了したか | `IsComplete` | `IsComplete` |
| GPU読戻し待ちが残っているか | `HasPendingReadback` | `HasPendingReadback` |
| 成功した結果 | `ResultBytes` | `ResultTexture` |
| 失敗理由。エラーがなければ空文字列 | `LastError` | `LastError` |

Encoderの設定は `Format`、`SubsampleChroma`、`InputTexture`、`Width`、`Height` で読み取れます。`Quality` だけは実行時にも変更でき、次の圧縮から使われます。その他の設定はEditorで変更し、作業用リソースを生成しておきます。

ボタンなどから引数なしのUdonイベントで圧縮を開始する場合は、`BeginEncoding()` を呼べます。InspectorのInput RenderTextureを使います。設定内容のエラーは `GetConfigurationError()` でも確認できます。通常の呼び出しではTryメソッドが検証するため、先に検証を重ねる必要はありません。

### 開始できない場合とキャンセル

- 処理中またはGPU読戻し待ちの間、Tryメソッドはfalseを返します。進行中の処理と、現在の結果・エラーは変更しません。キューには入りません。
- 入力や設定に問題がある場合もfalseを返し、`LastError` に理由を設定します。
- 開始後にGPUや画像の解析で失敗した場合は、`IsBusy` がfalse、`IsComplete` がfalseになり、`LastError` に理由が入ります。
- キャンセルは状態と結果参照をクリアします。発行済みのGPU処理は途中で停止できません。`HasPendingReadback` がfalseになってから再開してください。

処理の完了確認まで、Componentとその内部の子オブジェクトを有効にしておいてください。Materialと作業用Textureは実行時にインスタンスごとに用意します。Hierarchyで複製したコーデックも、それぞれ独立して使えます。一つのComponentが同時に処理できる画像は一枚です。複数のシステムで同じComponentを使う場合は、呼び出し側で順番を管理します。

## 結果はどこにあり、いつまで使えるか

結果はメモリ上にあります。自動でディスクへ保存したり、他のプレイヤーへ送信したりはしません。

| データ | 利用側で守ること |
| --- | --- |
| Encoderの`ResultBytes` | 完了ごとに新しい配列を返します。保持した配列は次の圧縮でも書き換えません。保存のためだけにコピーする必要はありません。共有中の配列は変更しないでください |
| Decoderへ渡す`byte[]` | コピーせず受け取ります。デコードが終了し、GPU読戻し待ちもなくなるまでは、中身を変更しないでください |
| Decoderの`ResultTexture` | Decoderが所有する作業用Textureです。次のデコードで再利用・置換されます。過去の画像を残す場合は、自分で用意したRenderTextureへ`VRCGraphics.Blit`でコピーしてください |

読み取り専用プロパティは、参照の差し替えを防ぐものです。配列の中身やTextureそのものを変更禁止にする仕組みではありません。コーデックのGameObjectを内部の子ごと破棄すると、作業用GPUリソースも破棄されます。必要な画像は先にコピーしてください。

## 既存バージョンからの移行

0.7ではアプリケーション向けAPIを整理しました。設定のシリアライズ名とスクリプトGUIDは維持していますが、スクリプトからの呼び出しは変更が必要です。

| 0.6の使い方 | 0.7の使い方 |
| --- | --- |
| `encoder.busy / complete / error / resultBytes` | `IsBusy / IsComplete / LastError / ResultBytes` |
| `decoder.inputBytes = bytes; decoder.BeginDecoding();` | `decoder.TryBeginDecoding(bytes)` |
| `decoder.resultTexture`などの状態フィールド | `ResultTexture`などの読み取り専用プロパティ |
| Encoderの設定フィールドを書き換える | Inspectorで設定。実行時の入力はTryメソッドへ渡し、画質は`Quality`で指定 |
| ランタイムに含まれていた`ImageCodecLink` | サンプルをインポート。名前空間は`VrcImageCodec.Samples` |

アップグレード後はEncoderのInspectorを開き、入力と設定を確認してください。0.6以前のDecoderはCreate decoderで作り直し、利用側の参照を差し替えてください。Editorスクリプトから生成する場合は `ImageEncoderSetup.Configure(encoder, input, format, width, height, quality, subsampleChroma)` を使えます。

`AvifEncoder`、`JpegEncoder`などの個別Componentは内部処理・ベンチマーク用です。UdonのComponent間呼び出しに必要なpublicメンバーは残っていますが、新規の利用側コードは上記の共通APIへ接続してください。

## ベンチマーク（0.7.0）

2026年10月26日、Unity 2022.3.22f1のClientSimで、コンパイル済みUdonと実際のGPUを使って測定しました。NVIDIA GeForce RTX 3050、AMD Ryzen 5 5600、Direct3D 11、Linear色空間、目標60fpsでの結果です。VRChatクライアントでの実測ではありません。

下表はQuality 60の三枚の写真の平均です。各条件は一回ずつ実行し、初回の準備時間も含みます。AVIFはFastスケジューリングで測定しています。公開Componentの初期設定はNormalのため、通常設定の待ち時間とは異なります。

| 形式 | 色差 | 解像度 | エンコード（秒） | デコード（秒） | バイナリ長（B） |
| --- | --- | --- | ---: | ---: | ---: |
| AVIF | 4:2:0 | 256×144 | 0.285 | 0.224 | 4,739 |
| AVIF | 4:4:4 | 256×144 | 0.286 | 0.251 | 6,497 |
| JPEG | 4:2:0 | 256×144 | 0.022 | 0.023 | 5,939 |
| JPEG | 4:4:4 | 256×144 | 0.022 | 0.034 | 8,110 |
| AVIF | 4:2:0 | 512×288 | 0.413 | 0.414 | 11,117 |
| AVIF | 4:4:4 | 512×288 | 0.459 | 0.503 | 15,554 |
| JPEG | 4:2:0 | 512×288 | 0.023 | 0.034 | 16,595 |
| JPEG | 4:4:4 | 512×288 | 0.023 | 0.032 | 25,019 |
| AVIF | 4:2:0 | 640×360 | 0.537 | 0.532 | 14,501 |
| AVIF | 4:4:4 | 640×360 | 0.538 | 0.653 | 20,438 |
| JPEG | 4:2:0 | 640×360 | 0.022 | 0.033 | 23,548 |
| JPEG | 4:4:4 | 640×360 | 0.025 | 0.030 | 36,189 |
| AVIF | 4:2:0 | 1280×720 | 0.955 | 1.274 | 34,197 |
| AVIF | 4:4:4 | 1280×720 | 0.936 | 1.600 | 49,384 |
| JPEG | 4:2:0 | 1280×720 | 0.023 | 0.034 | 70,420 |
| JPEG | 4:4:4 | 1280×720 | 0.026 | 0.030 | 120,654 |
| AVIF | 4:2:0 | 1920×1080 | 1.343 | 2.037 | 51,069 |
| AVIF | 4:4:4 | 1920×1080 | 1.370 | 2.533 | 74,274 |
| JPEG | 4:2:0 | 1920×1080 | 0.029 | 0.030 | 132,560 |
| JPEG | 4:4:4 | 1920×1080 | 0.044 | 0.023 | 243,224 |

時間は形式別コーデックの開始から完了通知までで、GPU処理と完了待ちを含みます。共通APIの入力コピー、撮影・通信・ファイル保存・検証処理は含みません。バイナリ長は圧縮ファイル自体の長さで、URLや暗号化のサイズではありません。異なる形式のQuality 60は、同等画質という意味ではありません。

**180条件すべてで外部照合に成功しました。** 各出力をFFmpeg 8.0.1で一回だけデコードし、GPUで復元した画像と寸法・向き・RGB値を照合しています。許容差は最大3/255、RMS 1.5/255です。Quality 60・70・80の個別結果と環境情報は[JSON](Documentation/Benchmarks/0.7.0.json)、表計算用データは[CSV](Documentation/Benchmarks/0.7.0.csv)にあります。

## 検証とライセンス

[ベンチマークの実行手順](https://github.com/gandalfr-KY/vrc-image-codec/blob/main/Tests~/README.md)には、別のPC・Unityプロジェクトで検証する方法を記載しています。三枚の写真、五段階の解像度、AVIF/JPEG、4:2:0/4:4:4、Quality 60/70/80の固定条件で、実行時間とバイナリ長を測ります。出力ごとにFFmpegで一回だけ復元し、画像を照合します。

処理時間はGPU・フレームレート・初回のShader準備などによって変わります。短時間で完了しても、途中のフレーム負荷が小さいとは限りません。採用先のワールドでも負荷を確認してください。

本プロジェクトで作成した部分はMITライセンスです。tinyavif・libaom由来の部分には元のBSD-2-Clauseと特許ライセンスが適用されます。[LICENSE](LICENSE)、[第三者の著作権表示](THIRD_PARTY_NOTICES.md)、[AOMedia特許ライセンス](PATENTS)を同梱しています。変更に参加する場合は[CONTRIBUTING.md](CONTRIBUTING.md)、内部構造を調べる場合は[実装資料](Documentation/Implementation.md)を参照してください。リリースを自分で作成する場合は、[GitHubのソース](https://github.com/gandalfr-KY/vrc-image-codec)を取得し、`python Tools~/pack.py` を実行してください。ソースzipには検証用の画像・ツールも含みます。
