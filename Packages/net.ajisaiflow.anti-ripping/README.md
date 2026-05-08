# Anti-Ripping (リッピング対策) — VRCAAR Repository

> **standalone リポジトリ**: `C:/code/unity/VRCAAR/`
> Unity プロジェクトには symlink で `Packages/Anti-Ripping` として読み込まれます。
> コードの編集はこのフォルダで行います (UnityAgent / MD3SDK と同じ pattern)。

## ディレクトリ構成

| パス | 内容 |
|---|---|
| `Editor/` | source (.cs) + asmdef + UI/ (banner / icon / social ロゴ) |
| `Runtime/` | AntiRippingTag.cs + asmdef |
| `Build~/` | 難読化ビルドパイプライン (Unity import 対象外、 dotnet + Obfuscar) |
| `Logs~/` | ビルドレポート / OSC manifest 出力 (Unity import 対象外) |
| `Generated/` | 生成 shader 等 |
| `Shaders/` | shader assets |
| `Prefabs/` | サンプルプレファブ配置用 |
| `package.json` | VPM manifest |
| `CHANGELOG.md` | バージョン履歴 |

## 開発フロー

1. このフォルダで `.cs` 等を直接編集
2. Unity プロジェクト (`com.ajisaiflow.vrchat.avater`) は `Packages/Anti-Ripping` symlink 経由で自動的に変更を検知 → 再コンパイル
3. リリース時は `Build~/build.ps1 -Version <semver>` で難読化 DLL をビルドし、 `Assets/紫陽花広場/VPM~/release.py` で VPM 配布リポジトリに publish

## 難読化ビルド

```powershell
cd Build~
./build.ps1 -Version 0.31.0   # またはそのまま実行で current version
```

出力: `Build~/bin/Release/Obfuscated/AjisaiFlow.AntiRipping.Editor.dll` (~295 KB)
アーカイブ: `Build~/Archive/v<ver>_<timestamp>/` に Mapping.txt + PDB + csproj snapshot を保存。

---

VRChat アバターの不正コピー（リッピング）に対する **アバター制作側の簡易対策** を提供する Unity エディタ拡張です。
ユーザー側の知識を一切要求せず、**アバタールートに 1 つコンポーネントを足すだけ** で機能します。

> **重要 — 完全な防止は技術的に不可能です**
> アバターはクライアント側で描画されるため、メモリや GPU からデータを抜くことは原理的に止められません。
> 本ツールの目的は **(1) コストを上げて casual ripper を弾く** と **(2) 流出物を作者に紐付ける証拠を残す** の 2 点です。

---

## 1. 既知のリッピング手法 (2024–2026 時点)

| # | 手法 | 仕組み | 現状 |
|---|---|---|---|
| A | **キャッシュ抽出** | `%LocalLow%/VRChat/VRChat/Cache-WindowsPlayer/` の `.vrca`/`__data` を AssetRipper / SARS で UnityPackage 化 | **2024 後半以降** VRChat が Asset Bundle Encryption + サーバー handshake を導入し、**新規アップロード分はほぼ無効化**。ただし暗号化前にアップロードされた古いアバターは依然抽出可能 |
| B | **改造クライアント** | MelonLoader 等で modded VRChat に dump 機能を仕込む | 古い手法だが現役。VRChat 側がクライアント整合性を強化しても完全根絶は困難 |
| C | **メモリダンプ / GPU リップ** | RenderDoc・Ninja Ripper などで描画コマンドを傍受しメッシュ/テクスチャを抽出 | プラットフォーム非依存で **常に有効**。シェーダー側で頂点を撹乱しないと防げない |
| D | **Hot-swap (キャッシュ差し替え)** | 暗号化前のキャッシュに別 avatar を置く昔ながらの手法 | 暗号化後は理論的に不可。古い VRCA に対しては有効 |
| E | **公式 API + 認証バイパス** | RipperStore 系のサービスが avatar id 経由で取得しているとされる | 規約違反。VRChat 側が継続的に対策中 |

### 既存対策の代表例

| ツール | 仕組み | 強み | 弱み |
|---|---|---|---|
| **AvaCrypt / Kanna Protecc** ([PlagueVRC/AntiRip](https://github.com/PlagueVRC/AntiRip)) | mesh の頂点を 32bit key で乱雑化 → 復号は専用 vertex shader 内 | 強力。手法 A/B でメッシュを抜かれてもジオメトリが破壊済み | Poiyomi/UTS など限定の shader 必須。lilToon は非対応。設定手順が多い |
| **GTAvaCryptV2LilToonUTS** ([Shell4026](https://github.com/Shell4026/GTAvaCryptV2LilToonUTS)) | UV を暗号化してから lilToon/UTS 化 | lilToon でも動く | 同上、設定手順が多い |
| **lilxyzw/AvaterEncryption** | AvaCrypt の自動化フォーク | 操作は楽 | 一部 BlendShape が壊れる |

### 本ツールが採る方針

「**完全防御ではなく、低コストで作者保護を最大化**」する設計です。

- ユーザー設定はアバタールートに `AntiRippingTag` を貼って Creator Name を入れるだけ
- VRChat SDK の Build & Upload 時に **NDMF パイプラインが自動実行**
- カスタムシェーダーを必須としない (どの shader でも動く)
- 副作用ゼロ: mesh/material の `name` 書換と非可視 GameObject 追加のみで、レンダリングや MA/AAO 連携を壊さない

---

## 2. 提供する保護レイヤー

| # | レイヤー | 効果 | 何を防ぐか |
|---|---|---|---|
| 1 | **Asset Watermark** | 全 Mesh / Material の `name` フィールドに `[AjisaiAR:作者名:buildID]` を追記 | リッパーが UnityPackage 化したとき、Project ビューに作者名がそのまま表示される。後付けで grep もヒットする |
| 2 | **Hierarchy Watermark** | アバター階層に作者署名を name とした不可視 GameObject を 3 箇所散布 | ヒエラルキーに作者情報が残る。再アップ前に手動で全部消す手間を強制する |
| 3 | **lilToon Vertex Hash** | lilToon マテリアルに `_AjisaiAR_Hash` を build id 由来の値で設定 | 流出した avatar とビルドレポートを突合し「どのビルドが漏れたか」を特定できる |
| 4 | **Build Fingerprint Report** | プロジェクト内 `Logs~/{avatar}_{creator}_{buildId}.md` にビルド情報を書き出す | 流出物を入手した際の追跡材料として手元に残る |

> 上記はいずれも **mesh ジオメトリを破壊しません**。AvaCrypt 系に比べると ripper が抜く事自体は止めませんが、抜いたあとに作者署名が **どのレイヤーにも複数同時に残る** ため再配布のリスクが上がります。

---

## 3. 使い方

### セットアップ (初回のみ)

1. **Window → 紫陽花広場 → Anti-Ripping** を開く
2. アバタールート (VRC Avatar Descriptor が付いた GameObject) を選択
3. 「**Anti-Ripping を有効化**」ボタン

### Inspector

`AntiRippingTag` の Inspector で:

- **Creator Name** (必須): ハンドル名 / サークル名
- **License URL** (推奨): BOOTH 商品 URL など
- **Contact** (任意): 連絡先

保護レイヤーは初期状態で **lilToon Vertex Hash 以外** ON。意図しない場合のみ OFF にしてください。

### ビルド

通常通り **VRChat SDK の Build & Publish for Windows** で OK。
NDMF パスが自動的に Transforming フェーズで走り、すべての保護を焼き込みます。
追加操作は不要です。

### 流出を見つけた場合

1. 抜かれたパッケージを Unity で開く
2. mesh / material の名前から `[AjisaiAR:...]` を検索 → buildId を取得
3. プロジェクト内 `Logs~/` から該当 buildId の `.md` を開いて当該ビルドの作成日時等を確認
4. ライセンス URL を添えて配布元 (BOOTH/Twitter/Discord) に DMCA / 削除依頼

---

## 4. 制限と既知の事項

- **Mesh ジオメトリ自体の暗号化はしません。** 抜かれたモデルは見た目通り使えます。視覚的な再配布完全防止には AvaCrypt / Kanna Protecc / GTAvaCryptV2 (Poiyomi/UTS 必須) を併用してください
- **GameObject 名の watermark は手動で消せます。** 1 つだけだと無効化されてしまうため、本ツールは複数箇所に散布します
- **VRChat 公式が暗号化済みの新規アバター** は手法 A の cache 抽出に対して既に強い保護があります。本ツールは「暗号化されていない経路 (modded client / GPU rip / 過去版抜き) で流出した場合に作者を辿れる」ことに価値があります
- mesh.name / material.name は基本的に動作に影響しませんが、**シェーダーが特殊な GetMaterial(by name) 検索を行う場合** に副作用の可能性があります。既知の主要シェーダー (lilToon/Poiyomi/UTS/Standard) は影響なし

---

## 5. v0.2: Mesh Lock (クローン時に表示を阻止)

v0.1 の watermark は **流出後の追跡** が目的で、表示自体は防げません。
v0.2 で追加された **Mesh Lock** は、**所有者以外がアバターをクローンしても破壊されたメッシュしか描画されない** ロジックを焼き込みます。

### 仕組み

1. **頂点スクランブル**: 各 SkinnedMeshRenderer の頂点位置を 32bit key を seed にした擬似乱数で ±0.5m 変位
2. **Unlock BlendShape**: `_AjisaiAR_Unlock` という BlendShape を追加 (delta = 元位置 − スクランブル位置)
   - weight=0 → スクランブル状態 (アバターが粉々)
   - weight=100 → 完全復元 (元の見た目)
3. **キーマッチ FSM**: AnimatorController に 4 つの int パラメータ (`_AjisaiAR_K0..K3`) を持つレイヤーを追加
   - 4 byte 全一致 → Unlocked 状態 (BlendShape weight=100)
   - いずれか不一致 → Locked 状態 (BlendShape weight=0)
4. **MA で注入**: Modular Avatar の MergeAnimator + Parameters でアバターに統合

### 重要な特徴

- **shader 非依存**: BlendShape は skinning パイプライン前段で適用されるため、lilToon / Poiyomi / Standard 等すべての shader で動作
- **同期しない**: パラメータは `localOnly=true`、他クライアントには値を見せない
- **デフォルト値 = 0 (ロック)**: クローンした側のセーブデータ初期値は全 0 → ロックされたまま
- **AAO/MA と非競合**: Transforming.AfterPlugin("modular-avatar") で実行

### キー配送方式

| モード | 動作 | 利便性 | 安全性 | 外部ツール |
|---|---|---|---|---|
| `SavedParameterViaOSC` (推奨) | OSC で 1 回値を書き込めば VRChat の保存パラメータから自動復元 | ◎ 1 回だけ | ○ | OSC sender (.ps1) |
| `OSCEverySession` | VRChat 起動 + 装着のたびに OSC 必要 | △ | ◎ | OSC sender (.ps1) |
| `ExpressionPIN` | Expression Menu で 1〜8 の数字を 4 桁入力 | ○ ゲーム内完結 | ○ (8⁴=4096通り) | 不要 |

### Expression PIN モードの詳細

Inspector で **Key Delivery = ExpressionPIN** を選び、4 桁の PIN (各桁 1〜8) を設定すると:

- **追加 FSM**: AnimatorController に `Wait_D0 → Wait_D1 → Wait_D2 → Wait_D3 → Done` の 5 状態 PIN 入力レイヤーが追加
- **Expression Menu 自動生成**: ルートメニューに **🔒 Unlock** が追加され、その下に 1〜8 の数字ボタン
- **入力ロジック**: `_AjisaiAR_PIN` に各ボタンが値をセット (Button 制御で押下時のみ送信) → 期待桁と一致なら次状態、不一致なら最初に戻る
- **連打誤発火防止**: 各 Wait 状態に入った瞬間 VRCAvatarParameterDriver で `_AjisaiAR_PIN = 0` にリセット
- **正解時**: Done 状態が Parameter Driver で `_AjisaiAR_K0..K3` を PIN 値で Set → 既存 AND 比較レイヤーが Unlocked へ遷移 → BlendShape 100 (復元)

ゲーム内ワークフロー:
```
1. VRChat 起動 → アバター装着 (この時点では破壊された状態で表示)
2. Expression Menu を開く
3. 🔒 Unlock を選択
4. 1〜8 の数字を 4 回タップ (例: 5 → 2 → 7 → 3)
5. 正解なら 1 ステップずつ進み、4 桁正解で復元
6. 間違えたらその場で Wait_D0 へ戻る → 最初から再入力
```

PIN モードのメリット/デメリット:
- ◎ OSC 不要、外部ツール不要、所有者は VRChat 内で完結
- ◎ 同じ Expression Menu はクローン側にも届くが、PIN を知らない限り 4096 通り総当たりが必要
- △ アバター装着のたびに毎回 4 桁入力が必要 (saved にすると cloner にも復元されてしまうため)
- △ デフォルト 1-2-3-4 のままだと意味がないので必ず変更すること

### 使い方

1. AntiRippingTag Inspector で **Mesh Lock** を ON
2. **Scramble Radius** を 0.3〜1.0 で設定
3. VRChat SDK で Build & Upload
4. **Logs~/{avatar}_{creator}_{buildId}_unlock.ps1** が生成される
5. VRChat 起動 → OSC 有効化 (Settings > OSC > Enabled) → アバター装着
6. `.ps1` を右クリック → **PowerShell で実行**
7. アバターが復元される (SavedParameterViaOSC モードなら以降自動)

### Mesh Lock の限界

- **パッケージ抽出には弱い**: AnimatorController の transition 条件として key 値が平文で入るため、`.controller` ファイルを解析されると鍵が抜ける
- **ゲーム内クローンには強い**: クローン側は AnimatorController 本体にアクセスできないため、4byte = 256^4 ≈ 43 億通りのブルートフォースが必要
- **OSC 必須**: VRChat の OSC 機能が無効化されている環境では unlock できない
- **Modular Avatar 必須**: Mesh Lock のみ MA に依存。他レイヤーは MA 不要

→ **AvaCrypt 系 (shader scramble + key)** と組み合わせると最強。本ツールはあくまで「shader 非依存で簡単に導入できる中位の防御層」を提供する。

---

## 6. AntiRippingClient (Windows 常駐アプリ)

毎回手動で .ps1 を実行するのは PC に弱いユーザーには負担が大きいので、
**VRChat 起動を自動検知して OSC 解錠キーを送信する Windows 常駐アプリ** を同梱しています。

### 場所

```
Build~/AntiRippingClient/
├── AntiRippingClient.csproj  ← .NET 8.0 WinForms プロジェクト
├── Program.cs / UI/ / Services/ / Models/
├── build.ps1                  ← single-file self-contained publish
└── README.md                  ← 詳細ガイド
```

### ビルド

```powershell
cd Build~/AntiRippingClient
./build.ps1
```

出力: `bin/Release/net8.0-windows/win-x64/publish/AntiRippingClient.exe` (~70MB single-file)。

### 動作の流れ

```
PC 起動
  └─ AntiRippingClient.exe (タスクトレイ常駐, Windows Startup 経由で自動起動)
       │
       ├─ VRChatWatcher  : Process 監視で VRChat.exe 起動を検知
       └─ OscListener    : UDP 9001 で /avatar/change を受信

VRChat 起動
  └─ アバター装着
       └─ /avatar/change avtr_xxxx → OscListener
              └─ レジストリから該当アバターを検索
                    └─ OscClient → UDP 9000 へ /avatar/parameters/_AjisaiAR_K0..K3 を送信
                          └─ アバターの FSM が unlock 条件成立 → BlendShape weight=100 → 復元
```

### ユーザー UX

1. アプリを 1 回起動
2. Unity が `Logs~/` に出力した **`*_unlock.json`** をウィンドウへドラッグ&ドロップ
3. `Windows 起動時に自動起動` チェック
4. 以後は **PC を起動して VRChat を起動するだけ** で自動的にアバター unlock

### 主要ファイル

| ファイル | 役割 |
|---|---|
| `Program.cs` | Mutex で多重起動防止 |
| `UI/TrayApplicationContext.cs` | タスクトレイ常駐、サービスのワイヤリング |
| `UI/MainForm.cs` | アバター一覧、Add/Remove/Send、自動起動 toggle |
| `Services/VRChatWatcher.cs` | Process polling で起動検知 (2 秒間隔) |
| `Services/OscClient.cs` | UDP 9000 への OSC int 送信 (手書き packet) |
| `Services/OscListener.cs` | UDP 9001 で `/avatar/change` を受信 |
| `Services/Startup.cs` | HKCU\\...\\Run への登録/解除 |
| `Models/UnlockManifest.cs` | Unity 出力 JSON のスキーマ |
| `Models/AvatarRegistry.cs` | `%APPDATA%/AjisaiFlow/AntiRippingClient/registry.json` |

### Unity 出力する 3 形式

ビルドごとに `Logs~/` に以下が並びます。お好みで:

| ファイル | 用途 | 自動化 |
|---|---|---|
| `..._unlock.ps1` | 単発の手動実行 (PowerShell) | × |
| `..._unlock.json` | **Windows アプリへ取り込む推奨形式** | ◎ |
| `....md` | 流出時の追跡用レポート | - |

---

## 7. 将来の拡張案 (TODO)

- ~~**Vertex scramble + decode shader (lilToon 対応)**~~ — ✅ v0.11+ で実装済 (`ShaderLockPass`)
- ~~**AAP / 累積パラメータ** による key 隠蔽~~ — ✅ v0.10+ で実装済 (`KeyAnimatorBuilder` Layer 1)
- **Texture pixel encryption** — texture asset の pixel data 自体を K で暗号化し、 shader 内で復号。 抽出した PNG が乱雑になる (= AssetRipper で抽出された .png/.jpg もそのままでは使えない)。 v0.31+ で新規実装予定
- **Texture 透かし (LSB ステガノ)** — テクスチャの最下位ビットに作者情報を埋め込み (Texture pixel encryption と独立した「追跡」レイヤー)
- **AntiRippingClient: アバター サムネイル表示** — VRChat API からプレビュー取得
- **AntiRippingClient: 配布 installer** — Inno Setup or MSIX 化
- **AntiRippingClient: BOOTH 連携** — 購入アバター用にライセンス制 manifest 配布

> **Note (v0.30 撤回)**: v0.30 で実装された **UV scramble** (mesh の UV0 を K で書き換える視覚的防御) は、 remote (相手) 視点で表示が破綻する問題が発覚したため撤回されました (v0.30.4)。 ユーザー要望に直接対応する **Texture pixel encryption** に方針転換しています。 詳細は memory `anti-ripping-v030-uv-scramble-rollback.md` 参照。

---

## 7. ファイル構成

```
anti-ripping/
├── README.md                            ← この文書
├── Runtime/
│   ├── AjisaiFlow.AntiRipping.Runtime.asmdef
│   └── AntiRippingTag.cs                ← 唯一のユーザー向けコンポーネント
└── Editor/
    ├── AjisaiFlow.AntiRipping.Editor.asmdef
    ├── AntiRippingPlugin.cs             ← NDMF プラグイン入口
    ├── AntiRippingTagEditor.cs          ← Inspector
    ├── AntiRippingWindow.cs             ← Window/紫陽花広場/Anti-Ripping
    └── Pipeline/
        ├── ProtectionReport.cs          ← Markdown レポート構築
        ├── WatermarkSignature.cs        ← 署名/buildId 生成
        ├── AssetWatermarkPass.cs        ← Mesh/Material name watermark
        ├── HierarchyWatermarkPass.cs    ← 不可視 GameObject 散布
        ├── LilToonProtectionPass.cs     ← lilToon マテリアル hash
        ├── FingerprintWriter.cs         ← Logs~ 書き出し
        ├── MeshLockData.cs              ← v0.2: key + 対象 SMR path
        ├── MeshLockPass.cs              ← v0.2: 頂点スクランブル + Unlock BlendShape
        ├── KeyAnimatorBuilder.cs        ← v0.2: FX FSM + MA 注入
        └── OSCSenderGenerator.cs        ← v0.2: 解錠用 .ps1 生成
```

---

## 8. 参考リンク

- [PlagueVRC/AntiRip (Kanna Protecc)](https://github.com/PlagueVRC/AntiRip)
- [rygo6/GTAvaCrypt](https://github.com/rygo6/GTAvaCrypt)
- [Shell4026/GTAvaCryptV2LilToonUTS](https://github.com/Shell4026/GTAvaCryptV2LilToonUTS)
- [lilxyzw/AvaterEncryption](https://github.com/lilxyzw/AvaterEncryption)
- [VRChat Avatar Asset Theft Feedback (公式)](https://feedback.vrchat.com/avatar-30/p/feedback-on-avatar-asset-theft-via-modified-clients-and-cache-hot-swapping)
