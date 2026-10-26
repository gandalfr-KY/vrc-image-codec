# VRC Image Codec

[日本語](#japanese) | [English](#english)

<a id="japanese"></a>

## 日本語

VRChatワールド（Unity）内で、RenderTextureをAVIFまたはJPEGに圧縮し、再びRenderTextureへ復元するUdonSharpライブラリです。変換にGPUを使用し、外部サーバーやネイティブプラグインは不要です。

```text
RenderTexture → ImageEncoder → byte[] → ImageDecoder → RenderTexture
```

画像の撮影、表示、通信、保存、暗号化、URL生成は、組み込むプロジェクトで実装してください。

### 動作環境と仕様

- **要件：** Unity 2022.3 / VRChat Worlds SDK 3.10.5以降（UdonSharp必須）。
- **プラットフォーム：** PC専用、Shader Model 5.0。Direct3D 11で検証済みです。Quest・モバイルは非対応で、他のGraphics APIは未検証です。
- **形式：** AVIF / JPEG、8-bit SDR、4:2:0または4:4:4の静止画。
- **解像度：** 幅・高さがそれぞれ16〜1920ピクセル、8の倍数、合計2,073,600ピクセル以下。縦長画像も使えます。これは本ライブラリ独自の制限で、現在の公開版は4K非対応です。
- **デコーダーの制約：** 本ライブラリで生成したデータの復元専用です。外部ソフトで作成した任意のAVIF・JPEG、アニメーション、HDR、アルファ、Progressive JPEGには対応していません。

生成したファイルは、その形式に対応した一般的な画像ビューアーでも開けます。

### 導入方法

[Releases](https://github.com/gandalfr-KY/vrc-image-codec/releases/latest)から本体の`.unitypackage`をダウンロードし、VRChat Worlds SDKを導入済みのUnityプロジェクトへインポートします。UPMで管理する場合は、配布zipの内容を`Packages/com.gandalfr.vrc-avif`へ配置してください。同じプロジェクトで両方の方法を併用しないでください。

1. **Tools > Image Codec > Create encoder**でエンコーダーを作成します。
2. Inspectorで**Input RenderTexture**と圧縮設定を指定します。
3. 復元も行う場合は、**Tools > Image Codec > Create decoder**でデコーダーを作成します。

#### エンコーダーの設定

Playモードに入る前に設定してください。

| 項目 | 説明 |
| --- | --- |
| Format | 圧縮形式（AVIF / JPEG） |
| Chroma sampling | 4:2:0（容量を節約）または4:4:4（色の細部を保持） |
| Input RenderTexture | 圧縮したい画像が描画されているRenderTexture |
| Resolution | 入力画像の縦横比を保つ出力サイズ |
| Quality | 1〜100、初期値60。高くすると通常は画質とデータ量が上がります |

入力の幅・高さも、それぞれ16以上、8の倍数にしてください。Resolutionには、縦横比を保ち、入力サイズと対応上限を超えない候補だけが表示されます。切り抜きや引き伸ばしは行いません。候補が一つしか出ない場合は、16:9・4:3・1:1などの比率で、より大きな入力を用意してください。

Decoderは形式・解像度・サンプリングを画像データから読み取るため、これらの設定は不要です。AVIFとJPEGでは、Qualityが同じ数値でも画質は同じになりません。

#### サンプルを試す

同じバージョンの`-sample.unitypackage`もインポートします。UPM版では、Package ManagerのSamplesから**Encode, decode and display**をインポートしてください。`Samples~/RoundTrip`をAssetsへコピーしても使えます。

1. Projectで16:9の画像を選びます。選ばなければテスト用の模様を使います。
2. **Tools > Image Codec > Create round-trip sample**を選びます。
3. Playモードに入り、**Encode & Decode**を押します。

左に入力画像、右に復元した画像、完了後に圧縮データのバイト数が表示されます。設定を変えるときはPlayモードを止め、子の**Image encoder**を選んでください。サンプルの`ImageCodecLink`では、Encoder・Decoder・表示先RawImageをInspectorで接続できます。

### UdonSharpからの利用

作成した`VrcImageCodec.ImageEncoder`と`VrcImageCodec.ImageDecoder`を、自分のUdonSharpスクリプトの参照欄へ割り当てて使います。通常のMonoBehaviourからUdonSharpのプロキシを直接呼び出す使い方には対応していません。

#### エンコード（圧縮）

```csharp
// InputTextureに画像を描画してから呼び出します。
bool accepted = encoder.TryBeginEncoding(encoder.InputTexture);
```

`true`は処理を開始できたことを表し、完了したという意味ではありません。処理は複数フレームにまたがるため、開始できた場合はUpdateなどで完了を待ちます。

```csharp
if (!encoder.IsBusy)
{
    if (encoder.IsComplete)
    {
        byte[] bytes = encoder.ResultBytes;
        // 取得したデータを保存・送信・デコードなどに使います。
    }
    else
    {
        Debug.LogError(encoder.LastError);
    }
}
```

結果を扱うのは、一回の開始につき一度だけにしてください。`IsComplete`は次の開始やキャンセルまで`true`のままです。サンプルの`ImageCodecLink`に、開始から結果の受け取りまでを管理する例があります。

`TryBeginEncoding`には別のRenderTextureも渡せます。入力サイズの条件と出力の縦横比を満たす必要があり、出力形式と解像度は設定済みの値を使います。開始時に内部へ画像をコピーするため、その後も入力RenderTextureへ描画を続けられます。

#### デコード（復元）

```csharp
bool accepted = decoder.TryBeginDecoding(bytes);
```

エンコーダーと同様に、開始できたら`IsBusy`と`IsComplete`で完了を確認します。成功時は`decoder.ResultTexture`、失敗時は`decoder.LastError`を取得します。画像を表示するには、取得したRenderTextureをRawImageやマテリアルへ割り当ててください。

<details>
<summary>API・キャンセルの詳細</summary>

| 用途 | Encoder | Decoder |
| --- | --- | --- |
| 開始 | `TryBeginEncoding(RenderTexture)` | `TryBeginDecoding(byte[])` |
| キャンセル | `CancelEncoding()` | `CancelDecoding()` |
| 処理中か | `IsBusy` | `IsBusy` |
| 正常に完了したか | `IsComplete` | `IsComplete` |
| GPUの読み戻し待ちが残っているか | `HasPendingReadback` | `HasPendingReadback` |
| 結果 | `ResultBytes` | `ResultTexture` |
| エラー（なければ空文字列） | `LastError` | `LastError` |

処理中やGPUの読み戻し待ちにTryメソッドを呼ぶと、`false`を返します。進行中の処理・結果・エラーは変わらず、後から自動実行されることもありません。入力や設定に問題がある場合も`false`を返し、`LastError`に理由が入ります。開始後に失敗した場合は、`IsBusy`と`IsComplete`がともに`false`になります。

キャンセルすると状態と結果の参照が初期化されます。すでにGPUへ送った処理は止められないため、`HasPendingReadback`が`false`になるまで再開を待ってください。完了を確認するまでは、コンポーネントとその子オブジェクトを有効にしておく必要があります。一つのコンポーネントで同時に処理できる画像は一枚です。複数のギミックで共有する場合は、順番に呼び出してください。Hierarchyで複製したコーデックは、それぞれ独立して使えます。

Encoderの設定値は`Format`、`SubsampleChroma`、`InputTexture`、`Width`、`Height`から取得できます。`Quality`だけは実行時にも変更でき、次の圧縮から反映されます。その他はEditorで設定してください。引数なしのUdonイベントでは`BeginEncoding()`を使うと、設定済みのInput RenderTextureを圧縮できます。設定の問題は`GetConfigurationError()`でも確認できますが、Tryメソッドも検証するため毎回先に呼ぶ必要はありません。

</details>

#### データの扱い

- **`ResultBytes`：** 完了ごとに新しい配列を返します。次の圧縮で上書きされないため、保持するためだけのコピーは不要です。他の処理と共有している間は中身を変更しないでください。
- **Decoderへ渡す`byte[]`：** コピーせずに使います。デコードとGPUの読み戻しが終わるまで、中身を変更しないでください。
- **`ResultTexture`：** Decoderが管理するTextureで、次のデコード時に再利用・置換されます。画像を残す場合は、自分のRenderTextureへ`VRCGraphics.Blit`でコピーしてください。コーデックのオブジェクトを子ごと破棄すると作業用GPUリソースも破棄されるため、コピーはその前に行ってください。

読み取り専用プロパティで返される配列やTextureも、中身まで変更禁止になるわけではありません。結果はメモリ上にあり、自動でファイル保存・同期・送信されることはありません。

### ベンチマーク（0.7.0）

2026年10月26日、Unity 2022.3.22f1のClientSimで測定しました。環境はNVIDIA GeForce RTX 3050、AMD Ryzen 5 5600、Direct3D 11、Linear色空間、目標60fpsです。VRChatクライアント内での測定ではありません。

Quality 60で三枚の写真を変換したときの平均です。各条件は一回ずつ実行し、初回の準備時間も含みます。AVIFはFast設定で測定しています。コンポーネントの初期設定はNormalのため、通常設定での待ち時間とは異なります。**JPEGは実行時間、AVIFはバイト長を太字で示しています。**

| 形式 | 色差 | 解像度 | エンコード（秒） | デコード（秒） | バイナリ長（B） |
| --- | --- | --- | ---: | ---: | ---: |
| AVIF | 4:2:0 | 256×144 | 0.285 | 0.224 | **4,739** |
| AVIF | 4:4:4 | 256×144 | 0.286 | 0.251 | **6,497** |
| JPEG | 4:2:0 | 256×144 | **0.022** | **0.023** | 5,939 |
| JPEG | 4:4:4 | 256×144 | **0.022** | **0.034** | 8,110 |
| AVIF | 4:2:0 | 512×288 | 0.413 | 0.414 | **11,117** |
| AVIF | 4:4:4 | 512×288 | 0.459 | 0.503 | **15,554** |
| JPEG | 4:2:0 | 512×288 | **0.023** | **0.034** | 16,595 |
| JPEG | 4:4:4 | 512×288 | **0.023** | **0.032** | 25,019 |
| AVIF | 4:2:0 | 640×360 | 0.537 | 0.532 | **14,501** |
| AVIF | 4:4:4 | 640×360 | 0.538 | 0.653 | **20,438** |
| JPEG | 4:2:0 | 640×360 | **0.022** | **0.033** | 23,548 |
| JPEG | 4:4:4 | 640×360 | **0.025** | **0.030** | 36,189 |
| AVIF | 4:2:0 | 1280×720 | 0.955 | 1.274 | **34,197** |
| AVIF | 4:4:4 | 1280×720 | 0.936 | 1.600 | **49,384** |
| JPEG | 4:2:0 | 1280×720 | **0.023** | **0.034** | 70,420 |
| JPEG | 4:4:4 | 1280×720 | **0.026** | **0.030** | 120,654 |
| AVIF | 4:2:0 | 1920×1080 | 1.343 | 2.037 | **51,069** |
| AVIF | 4:4:4 | 1920×1080 | 1.370 | 2.533 | **74,274** |
| JPEG | 4:2:0 | 1920×1080 | **0.029** | **0.030** | 132,560 |
| JPEG | 4:4:4 | 1920×1080 | **0.044** | **0.023** | 243,224 |

時間は形式別コーデックの開始から完了通知までで、GPU処理と完了待ちを含みます。共通APIの入力コピー、撮影・通信・保存・検証処理は含みません。バイナリ長は圧縮ファイル自体のバイト数で、暗号化やURL化による増加分は含みません。異なる形式のQuality 60は、同じ画質を表すものではありません。

**全180条件でFFmpegとの画像比較に合格しました。** 各出力をFFmpeg 8.0.1で一回ずつデコードし、GPUで復元した画像と寸法・向き・RGB値を比較しています。許容差は最大3/255、RMS 1.5/255です。Quality 60・70・80の全結果は[JSON](Documentation/Benchmarks/0.7.0.json)・[CSV](Documentation/Benchmarks/0.7.0.csv)、再現手順は[ベンチマークの実行手順](https://github.com/gandalfr-KY/vrc-image-codec/blob/main/Tests~/README.md)にあります。同梱の三枚の写真、五段階の解像度、二つの形式、二つのサンプリング、三つのQualityを使います。

処理時間はGPUやフレームレートなどで変わります。短時間で完了しても、一フレームに大きな負荷がかかる場合があるため、組み込むワールドでも負荷を確認してください。

<details>
<summary>0.6以前からの移行</summary>

0.7では公開APIを整理しました。保存済み設定のシリアライズ名とスクリプトのGUIDは維持していますが、呼び出しコードは変更が必要です。

| 0.6 | 0.7 |
| --- | --- |
| `encoder.busy / complete / error / resultBytes` | `IsBusy / IsComplete / LastError / ResultBytes` |
| `decoder.inputBytes = bytes; decoder.BeginDecoding();` | `decoder.TryBeginDecoding(bytes)` |
| `decoder.resultTexture`などの状態フィールド | `ResultTexture`などの読み取り専用プロパティ |
| Encoderの設定フィールドを変更 | Inspectorで設定。実行時の画像はTryメソッドへ渡し、画質は`Quality`で指定 |
| 本体に含まれていた`ImageCodecLink` | サンプルに移動。名前空間は`VrcImageCodec.Samples` |

更新後はEncoderのInspectorで入力と設定を確認してください。0.6以前のDecoderは**Create decoder**で作り直し、自分のスクリプトやギミックの参照を差し替えてください。

Editorスクリプトから生成する場合は、`ImageEncoderSetup.Configure(encoder, input, format, width, height, quality, subsampleChroma)`を使えます。`AvifEncoder`や`JpegEncoder`は内部実装・ベンチマーク用です。新しいギミックでは`ImageEncoder`・`ImageDecoder`を使ってください。

</details>

### ライセンス・開発資料

独自に作成したコードはMITライセンスです。tinyavif・libaom由来のコードには、元のBSD-2-ClauseとAOMedia特許ライセンスが適用されます。詳細は[LICENSE](LICENSE)、[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)、[PATENTS](PATENTS)を確認してください。

開発への参加は[CONTRIBUTING.md](CONTRIBUTING.md)、内部構造は[実装資料](Documentation/Implementation.md)を参照してください。配布パッケージを作る場合は、ソースを取得して`python Tools~/pack.py`を実行します。ソースzipには検証用の画像とツールも含まれます。

<a id="english"></a>

## English

A UdonSharp library for VRChat worlds that compresses RenderTextures into AVIF or JPEG and decodes them back into RenderTextures. It uses the GPU for image processing and requires no external server or native plugin.

```text
RenderTexture → ImageEncoder → byte[] → ImageDecoder → RenderTexture
```

Implement capturing, display, networking, storage, encryption, and URL generation in your own project.

### Requirements and specifications

- **Environment:** Unity 2022.3 / VRChat Worlds SDK 3.10.5+ with UdonSharp.
- **Platform:** PC, Shader Model 5.0. Tested on Direct3D 11. Quest and mobile are unsupported; other graphics APIs are untested.
- **Formats:** AVIF / JPEG, 8-bit SDR, 4:2:0 or 4:4:4 still images.
- **Resolution:** Each dimension must be 16–1920 pixels and a multiple of 8, with at most 2,073,600 pixels in total. Portrait images are supported. This is a library-specific limit; the current release does not support 4K.
- **Decoder scope:** Only the profiles produced by this library are supported. Arbitrary external AVIF/JPEG files, animation, HDR, alpha, and Progressive JPEG are unsupported.

Encoded files can also be opened in standard viewers that support their format.

### Installation and setup

Download the core `.unitypackage` from [Releases](https://github.com/gandalfr-KY/vrc-image-codec/releases/latest) and import it into a Unity project with the VRChat Worlds SDK installed. For UPM, extract the package zip into `Packages/com.gandalfr.vrc-avif`. Do not install both variants in the same project.

1. Select **Tools > Image Codec > Create encoder**.
2. Assign **Input RenderTexture** and configure compression in its Inspector.
3. To decode images, select **Tools > Image Codec > Create decoder**.

#### Encoder settings

Configure these before entering Play mode.

| Setting | Description |
| --- | --- |
| Format | AVIF or JPEG |
| Chroma sampling | 4:2:0 saves space; 4:4:4 preserves fine color detail |
| Input RenderTexture | The RenderTexture containing the image to compress |
| Resolution | Output size preserving the input aspect ratio |
| Quality | 1–100, default 60. Higher values generally increase quality and file size |

Input dimensions must also be at least 16 and multiples of 8. Resolution offers only sizes that preserve the aspect ratio and do not exceed the input size or supported limits. No cropping or stretching is performed. If only one option appears, use a larger input with a standard ratio such as 16:9, 4:3, or 1:1.

The decoder reads format, resolution, and sampling from the encoded data. No corresponding settings are needed. Equal Quality values in AVIF and JPEG do not mean equal visual quality.

#### Try the sample

Import the matching `-sample.unitypackage`. For UPM, import **Encode, decode and display** from Samples in the Package Manager, or copy `Samples~/RoundTrip` into Assets.

1. Select a 16:9 image in Project, or leave it unselected to use a test pattern.
2. Select **Tools > Image Codec > Create round-trip sample**.
3. Enter Play mode and press **Encode & Decode**.

The input appears on the left and the decoded image on the right. The encoded byte count appears on completion. To change settings, exit Play mode and select the child **Image encoder**. The sample's `ImageCodecLink` connects an encoder, decoder, and target RawImage through the Inspector.

### Usage from UdonSharp

Assign the generated `VrcImageCodec.ImageEncoder` and `VrcImageCodec.ImageDecoder` components to reference fields in your UdonSharp script. These APIs are for UdonSharp; directly calling the Editor proxies from a regular MonoBehaviour is unsupported.

#### Encoding

```csharp
// Render the image into InputTexture before starting.
bool accepted = encoder.TryBeginEncoding(encoder.InputTexture);
```

`true` means the request was accepted, not that encoding has finished. Processing spans multiple frames. After an accepted request, wait for completion in Update or similar logic.

```csharp
if (!encoder.IsBusy)
{
    if (encoder.IsComplete)
    {
        byte[] bytes = encoder.ResultBytes;
        // Pass the data to your storage, networking, or decoding logic.
    }
    else
    {
        Debug.LogError(encoder.LastError);
    }
}
```

Handle each accepted request's result only once. `IsComplete` stays true until the next start or cancellation. The sample's `ImageCodecLink` demonstrates tracking a request through completion.

You may pass a different RenderTexture to `TryBeginEncoding`. It must satisfy the input size rules and match the configured output aspect ratio. Output format and resolution use the preconfigured values. The encoder snapshots the image at the start, so you can continue rendering into the source afterward.

#### Decoding

```csharp
bool accepted = decoder.TryBeginDecoding(bytes);
```

After an accepted request, check `IsBusy` and `IsComplete` as with encoding. On success, read `decoder.ResultTexture`; on failure, read `decoder.LastError`. Assign the returned RenderTexture to a RawImage or material to display it.

<details>
<summary>API and cancellation details</summary>

| Purpose | Encoder | Decoder |
| --- | --- | --- |
| Start | `TryBeginEncoding(RenderTexture)` | `TryBeginDecoding(byte[])` |
| Cancel | `CancelEncoding()` | `CancelDecoding()` |
| Processing | `IsBusy` | `IsBusy` |
| Completed successfully | `IsComplete` | `IsComplete` |
| GPU readback still pending | `HasPendingReadback` | `HasPendingReadback` |
| Result | `ResultBytes` | `ResultTexture` |
| Error, or empty string if none | `LastError` | `LastError` |

A Try call while busy or waiting for a GPU readback returns `false`, leaving the ongoing operation, result, and error unchanged. The request is not queued. Invalid input or settings also return `false` and set `LastError`. A failure after starting leaves both `IsBusy` and `IsComplete` false.

Cancellation resets state and result references. Already dispatched GPU work cannot be stopped; wait until `HasPendingReadback` is false before restarting. Keep the component and its children active until completion. Each component processes one image at a time; serialize calls when sharing it between systems. Duplicated codec hierarchies operate independently.

Read encoder settings through `Format`, `SubsampleChroma`, `InputTexture`, `Width`, and `Height`. Only `Quality` can change at runtime, applying to the next encode. Configure other settings in the Editor. A parameterless Udon event can call `BeginEncoding()` to use the configured Input RenderTexture. `GetConfigurationError()` checks settings, but Try calls already validate input, so a separate check is optional.

</details>

#### Data ownership

- **`ResultBytes`:** Each completion returns a new array. Later encodes do not overwrite it, so no copy is needed just to retain it. Do not modify its contents while sharing it with other operations.
- **`byte[]` passed to the decoder:** Used without copying. Do not modify it until decoding and pending GPU readbacks have finished.
- **`ResultTexture`:** Owned by the decoder and reused or replaced on the next decode. To retain an image, copy it to your own RenderTexture with `VRCGraphics.Blit`. Destroying the codec hierarchy also destroys its working GPU resources, so copy any needed image first.

Read-only properties do not make array or texture contents immutable. Results reside in memory; no automatic file saving, synchronization, or transmission takes place.

### Benchmarks (0.7.0)

Measured on October 26, 2026 in Unity 2022.3.22f1 ClientSim with an NVIDIA GeForce RTX 3050, AMD Ryzen 5 5600, Direct3D 11, Linear color space, and a target of 60 fps. These are not VRChat client measurements.

Values are the mean across three photographs at Quality 60. Each case ran once, including first-use preparation costs. AVIF used Fast scheduling; the component default is Normal, so default wait times differ. **JPEG execution times and AVIF byte sizes are bold.**

| Format | Chroma | Resolution | Encode (s) | Decode (s) | Binary size (B) |
| --- | --- | --- | ---: | ---: | ---: |
| AVIF | 4:2:0 | 256×144 | 0.285 | 0.224 | **4,739** |
| AVIF | 4:4:4 | 256×144 | 0.286 | 0.251 | **6,497** |
| JPEG | 4:2:0 | 256×144 | **0.022** | **0.023** | 5,939 |
| JPEG | 4:4:4 | 256×144 | **0.022** | **0.034** | 8,110 |
| AVIF | 4:2:0 | 512×288 | 0.413 | 0.414 | **11,117** |
| AVIF | 4:4:4 | 512×288 | 0.459 | 0.503 | **15,554** |
| JPEG | 4:2:0 | 512×288 | **0.023** | **0.034** | 16,595 |
| JPEG | 4:4:4 | 512×288 | **0.023** | **0.032** | 25,019 |
| AVIF | 4:2:0 | 640×360 | 0.537 | 0.532 | **14,501** |
| AVIF | 4:4:4 | 640×360 | 0.538 | 0.653 | **20,438** |
| JPEG | 4:2:0 | 640×360 | **0.022** | **0.033** | 23,548 |
| JPEG | 4:4:4 | 640×360 | **0.025** | **0.030** | 36,189 |
| AVIF | 4:2:0 | 1280×720 | 0.955 | 1.274 | **34,197** |
| AVIF | 4:4:4 | 1280×720 | 0.936 | 1.600 | **49,384** |
| JPEG | 4:2:0 | 1280×720 | **0.023** | **0.034** | 70,420 |
| JPEG | 4:4:4 | 1280×720 | **0.026** | **0.030** | 120,654 |
| AVIF | 4:2:0 | 1920×1080 | 1.343 | 2.037 | **51,069** |
| AVIF | 4:4:4 | 1920×1080 | 1.370 | 2.533 | **74,274** |
| JPEG | 4:2:0 | 1920×1080 | **0.029** | **0.030** | 132,560 |
| JPEG | 4:4:4 | 1920×1080 | **0.044** | **0.023** | 243,224 |

Times cover the format-specific codec's start through completion, including GPU processing and completion waits. Common API input copying, capturing, networking, file saving, and validation are excluded. Binary size is the encoded file size, excluding encryption and URL overhead. Quality 60 does not imply equal visual quality across formats.

**All 180 cases passed external image comparison.** Each output was decoded once using FFmpeg 8.0.1 and compared with the GPU reconstruction for dimensions, orientation, and RGB values. Tolerance: maximum 3/255, RMS 1.5/255. Full Quality 60/70/80 results are available as [JSON](Documentation/Benchmarks/0.7.0.json) and [CSV](Documentation/Benchmarks/0.7.0.csv). Follow the [benchmark guide](https://github.com/gandalfr-KY/vrc-image-codec/blob/main/Tests~/README.md) to reproduce the matrix: three bundled photos, five resolutions, two codecs, two samplings, and three quality settings.

Timing varies with GPU and frame rate. A short completion time can still include a large per-frame workload; profile the codec in your target world.

<details>
<summary>Migration from 0.6 and earlier</summary>

Version 0.7 reorganizes the public API. Serialized setting names and script GUIDs are preserved, but calling code must change.

| 0.6 | 0.7 |
| --- | --- |
| `encoder.busy / complete / error / resultBytes` | `IsBusy / IsComplete / LastError / ResultBytes` |
| `decoder.inputBytes = bytes; decoder.BeginDecoding();` | `decoder.TryBeginDecoding(bytes)` |
| State fields such as `decoder.resultTexture` | Read-only properties such as `ResultTexture` |
| Modifying encoder configuration fields | Configure in Inspector. Pass runtime images to Try methods and set quality through `Quality` |
| `ImageCodecLink` in the core runtime | Import the sample; namespace is `VrcImageCodec.Samples` |

After upgrading, check input and settings in the encoder Inspector. Recreate decoders from 0.6 or earlier using **Create decoder** and update references in your scripts or other components.

Editor integrations can use `ImageEncoderSetup.Configure(encoder, input, format, width, height, quality, subsampleChroma)`. `AvifEncoder` and `JpegEncoder` are internal implementations and benchmark components; use `ImageEncoder` and `ImageDecoder` in new integrations.

</details>

### License and development

Project-authored code uses the MIT License. Portions derived from tinyavif and libaom retain their BSD-2-Clause and AOMedia patent licenses. See [LICENSE](LICENSE), [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), and [PATENTS](PATENTS).

See [CONTRIBUTING.md](CONTRIBUTING.md) to contribute, and [implementation notes](Documentation/Implementation.md) for internals. To build distribution packages, obtain the source and run `python Tools~/pack.py`. The source zip includes verification images and tools.
