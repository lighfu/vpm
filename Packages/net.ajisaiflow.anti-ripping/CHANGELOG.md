# Changelog — Anti-Ripping

All notable changes to this VPM package.

## Client [0.14.0] — 2026-05-06 (Watcher Mode 追加)

> Windows 常駐アプリ (`Build~/AntiRippingClient/AntiRippingClient.exe`) のみの変更。 Unity パッケージ側に変更はなし。

### Added

「VRChat を起動した瞬間にだけ tray icon が立ち上がる」自動起動挙動を実現する **Watcher Mode** を追加。 desktop / VR どちらの起動経路でも動作する。

#### 新しい状態機械

```
[Watching] ←─────────────────── VRChat 終了 + MainForm 非表示
   │
   │ VRChat 起動 / IPC ShowUi signal / ユーザー明示起動
   ↓
[Active]  →  tray icon 表示、 OSC listener 起動、 通常動作
```

- **Watching** : tray icon 非表示、 `OscListener` 停止、 idle daemon 状態。 polling だけが動いており CPU/メモリ消費は最小
- **Active** : tray icon 表示、 OSC listener 起動、 v0.13 と同じ通常動作

#### 状態遷移トリガ

| イベント | from → to |
|---|---|
| VRChat.exe 起動検知 (VRChatWatcher 2sec polling) | Watching → Active |
| VRChat.exe 終了検知 + MainForm 非表示 | Active → Watching |
| ユーザーが exe を double-click | (任意) → Active + MainForm 表示 |
| `--tray` 引数なしで明示起動 | 起動直後から Active |

VRChat の起動経路は問わない (Steam/launcher/ショートカット/VR/desktop)。 単に VRChat.exe プロセスの存在を polling しているだけなので、 どの経路でも反応する。

#### IPC for double-click promotion

ユーザーが既に Watching mode で常駐中の AntiRippingClient.exe を double-click すると、 二重起動 mutex で blocked される。 v0.14 では blocked された second-instance が `Global\AjisaiFlow.AntiRippingClient.ShowUI` 名前付き `EventWaitHandle` を `Set()` し、 既存インスタンスが背景スレッドで Wait → Active に promote + MainForm 表示する。 既存インスタンスが旧版 (v0.13 以前) で event handle が未定義のときは fallback で従来の MessageBox を表示。

#### MainForm 設定

新しい checkbox 「VRChat 起動中だけ tray アイコンを表示 (省リソース)」 を Windows 自動起動 checkbox の下に追加。 default ON (= 新挙動)。 OFF にすると v0.13 以前の legacy 挙動 (= tray 常駐) に戻る。

`ClientSettings.EnableWatcherMode` (bool, default `true`) で永続化。 既存ユーザーは registry.json に該当フィールドが無いため、 deserialize 時に default 値が適用され、 自動的に新挙動になる。

### Changed

- **`TrayApplicationContext`** : Mode (Watching / Active) 切替に対応するよう全面 refactor。 tray icon と OSC listener は mode に応じて create/dispose / start/stop されるようになった
- **`Program.cs`** : 二重起動 blocked 時に `--tray` 無し (= ユーザー明示起動) なら IPC ShowUi event を Set してから silent exit。 IPC 失敗時のみ MessageBox fallback
- **`MainForm` constructor** : `TrayApplicationContext? trayContext` を optional 引数として追加。 watcher mode toggle 変更を tray context に通知する用途
- **MainForm レイアウト** : footer を 3 行 → 4 行に拡張 (新行: watcher mode toggle)。 `ClientSize.Height` 800 → 830

### Migration

| ユーザー区分 | 挙動 |
|---|---|
| 新規 v0.14 install | watcher mode default ON、 VRChat 起動中だけ tray が出る |
| v0.13 → v0.14 update | watcher mode 自動 ON、 同上挙動。 違和感あれば MainForm の checkbox で OFF にすれば legacy 挙動に戻る |
| Run reg key | `--tray` のまま変更なし。 watcher mode は arg ではなく setting で切替えるため reg key 操作不要 |

### Files

- 変更 : `Build~/AntiRippingClient/Models/AvatarRegistry.cs` (`EnableWatcherMode` setting 追加)
- 変更 : `Build~/AntiRippingClient/Program.cs` (IPC ShowUi event signal 送信)
- 変更 : `Build~/AntiRippingClient/UI/TrayApplicationContext.cs` (mode 切替対応に全面 refactor)
- 変更 : `Build~/AntiRippingClient/UI/MainForm.cs` (Watcher Mode toggle 追加、 layout 1 行追加)
- 変更 : `Build~/AntiRippingClient/UI/Localizer.cs` (`main.watcher_mode` を 4 言語追加)
- 変更 : `Build~/AntiRippingClient/AntiRippingClient.csproj` (Version `0.13.0` → `0.14.0`)

### Verification

1. `dotnet build -c Release` → 0 警告 0 エラー
2. `dotnet publish -c Release` で single-file exe 生成
3. **Watching → Active 遷移テスト**: AntiRippingClient.exe `--tray` 起動 → tray アイコン無し → VRChat 起動 → 数秒以内に tray アイコン出現 → OSC unlock 動作確認
4. **Active → Watching 遷移テスト**: VRChat 終了 → 数秒以内に tray アイコン消失
5. **IPC double-click test**: tray 非表示状態で exe を double-click → MainForm が出現すること
6. **Legacy mode test**: MainForm で watcher mode OFF → tray アイコン常時表示 (v0.13 と同じ動作) になること

## Client [0.13.0] — 2026-05-06 (Steam 起動連携機能を追加)

> Windows 常駐アプリ (`Build~/AntiRippingClient/AntiRippingClient.exe`) のみの変更。 Unity パッケージ側に変更はなし。

### Added

「VRChat 起動と連動して自動起動」 オプションを追加。 Windows ログオン時の常駐 (= 既存方式) を使わずに、 Steam から VRChat を起動した瞬間だけ AntiRipping Client を立ち上げたいユーザー向け。

#### 仕組み

Steam の VRChat 起動オプションに以下の文字列を仕込むと、 VRChat 起動の直前に AntiRipping Client が tray 起動する:

```
cmd /c start "" "C:\Users\<user>\AppData\Local\AjisaiFlow\AntiRippingClient\AntiRippingClient.exe" --tray & %command%
```

- `cmd /c start ""` — 別プロセスとして非同期起動 (VRChat 起動を待たない)
- `--tray` — tray 常駐モード。 既に起動中なら silent exit するので衝突しない
- `%command%` — Steam が VRChat を起動するコマンド (Steam が展開する)

#### UI

- **MainForm** : Windows 自動起動 checkbox の右隣に「🎮 Steam 起動連携...」ボタンを追加
- **Tray menu** : 「Steam 起動連携を設定...」項目を追加
- **Steam Setup Dialog** :
  - 推奨 launch option 文字列を表示 (read-only TextBox + monospace font)
  - 「📋 クリップボードにコピー」ボタン
  - 「🎮 Steam の VRChat ページを開く」ボタン (Steam クライアントで `steam://nav/games/details/438100` を起動)
  - ステップバイステップ手順 (5 ステップ) + 動作説明 + 注意事項

直接 `localconfig.vdf` を書き換える方式は Steam が起動中だと巻き戻されるため、 ユーザーに文字列を手動で貼り付けてもらう運用にした。

### Changed

- **`Program.cs`** : `--tray` 引数付き起動時に多重起動 (= mutex 取得失敗) を検出したら **silent exit** に変更。 これまでは「既に起動中」 MessageBox が出ていたため、 Steam launch options 経由で VRChat 起動の度に popup が出る問題を防ぐ。 ユーザーが手動で `.exe` を double click した場合 (`--tray` なし) は従来通り MessageBox を表示する。

### Files

- 新規 : `Build~/AntiRippingClient/Services/SteamIntegration.cs` (Steam path 検出 + launch option 生成)
- 新規 : `Build~/AntiRippingClient/UI/SteamLaunchSetupDialog.cs` (設定ダイアログ)
- 変更 : `Build~/AntiRippingClient/Program.cs` (silent exit)
- 変更 : `Build~/AntiRippingClient/UI/MainForm.cs` (ボタン追加)
- 変更 : `Build~/AntiRippingClient/UI/TrayApplicationContext.cs` (tray menu 項目追加)
- 変更 : `Build~/AntiRippingClient/UI/Localizer.cs` (`steam.dialog.*` / `main.btn.steam_setup` / `tray.menu.steam_setup` を 4 言語追加)
- 変更 : `Build~/AntiRippingClient/AntiRippingClient.csproj` (Version `0.12.12` → `0.13.0`)

### Verification

1. `Build~/AntiRippingClient/build.ps1` で publish
2. Inspector の「インストール」ボタンで `%LOCALAPPDATA%/AjisaiFlow/AntiRippingClient/` に配備
3. tray アイコンを開き、 「Steam 起動連携...」 ボタンで dialog が出ることを確認
4. クリップボードコピーが動作 + Steam が起動して VRChat ページが開くことを確認
5. Steam の VRChat 起動オプションに貼り付け → VRChat 起動 → tray アイコンが先に出ることを確認
6. 既に起動中の状態で再度 VRChat を起動しても MessageBox が出ないこと (silent exit)

## [0.34.0] — 2026-05-07 (Universal LIL_SAMPLE_* wrapper framework — stable)

> v0.34.0-alpha.1 (下記参照) を Unity 実機 variant matrix 検証 (lts / ltsmulti / ltsmulti_ref / ltsl / lts_fur / lts_gem / lts_tess × Forward / ForwardAdd / Outline / Meta / Shadow / DepthOnly) で問題なし確認 → stable 化。

### Changed

- `enableLilToonUniversalWrapper` master toggle を撤去。 alpha 期間の cross-validation 用に「OFF で旧 OVERRIDE_* path に短絡」する選択肢を残していたが、 wrapper 経路が安定したため不要。 `Runtime/AntiRippingTag.cs` の SerializeField + public getter、 `Editor/AntiRippingPlugin.cs` の log 出力から削除。 既存 user の Inspector 表示には影響なし (default ON のため挙動変化なし)。

### Verification

- 公式 lilToon shader 仕様準拠 audit 完了 (`Documentation~/audit/lilToon-compliance-2026-05-07.md`、 ~600 行) → 既存 3 prop の wrapper 経路に regression なし
- v0.34.0-alpha.1 が報告した 4 lilToon variant 個別 compile に加え、 7 variant × 6 pass の variant matrix で実機検証通過

### Roadmap

v0.34.1+ で予定していた Mask group activate は audit Finding (B / C) を踏まえ v0.34.6+ に延期 (詳細は `Documentation~/handoff/2026-05-07-handoff.md`)。 次の段階 release は v0.34.2 Color group (Finding A `lilGetSubTex` token paste 解決後)。

---

## [0.34.0-alpha.1] — 2026-05-07 (Universal LIL_SAMPLE_* wrapper framework — alpha 1)

> 段階導入の最初のステップ。 lilToon の全 texture sampling を universal wrapper 経由に再設計するための **framework 構築 + 既存 3 prop の wrapper 経路移行**。 既存挙動 100% 互換、 全 group toggle が default OFF (opt-in)。

### Context

v0.33.x までは `_MainTex` / `_BumpMap` / `_AlphaMask` の 3 prop のみ暗号化、 残り ~50 個の lilToon auxiliary texture (`_Main2ndTex`, `_MatCapBlendMask`, `_EmissionMap`, `_OutlineWidthMask`, `_RimColorTex` 等) は AssetRipper 抽出で素通し leak していた。 user 検証 (chiffon-spring avatar) で face decal / mask / emission texture が大量に見える状態を確認。

過去 v0.31.13 で `_EmissionMap` を `OVERRIDE_EMISSION_1ST` inline 展開で対応しようとして、 lilToon variant (LIL_LITE / MULTI / REFRACTION × Forward / Outline / Meta / Shadow pass) で `fd.invLighting` / `fd.albedo` / `lilCalcBlink` 等が未定義 → compile error → 全 renderer fallback → 全 texture 露出という致死 regression を起こした。

v0.34.x では **architectural rewrite** で全 lilToon texture を統一的に暗号化対象にする。 5 段階 release で段階的に category を活性化 (Mask → Color → Normal → Emission)。 user の主要関心事 (face decal `_Main2ndTex` / `_Main3rdTex`) は **v0.34.2** で解決予定。

### Added

#### Universal LIL_SAMPLE_* wrapper framework

lilToon の `LIL_SAMPLE_2D` / `LIL_SAMPLE_2D_ST` / `LIL_SAMPLE_2D_LOD` / `LIL_SAMPLE_2D_GRAD` / `LIL_SAMPLE_2D_BIAS` macro family を **HLSL preprocessor token paste** で per-property specialization に redirect する universal wrapper 機構を追加。

```hlsl
#undef  LIL_SAMPLE_2D_ST
#define LIL_SAMPLE_2D_ST(tex, samp, uv) _AR_LilSampleST_##tex(samp, uv)
//                                         ^^^^^^^^^^^^^^^^^^^^^^^
//                                         tex == "_MainTex" → _AR_LilSampleST__MainTex
```

各 lilToon prop に対し specialization 関数を emit:
- 暗号化対象 (= spec 配列に entry あり) → seed != 0 のとき snap+decode、 seed = 0 のとき raw passthrough
- 非対象 → 常に raw passthrough (lilToon body 内 sample が我々の wrapper 経由で解決される必要があるため必須)

#### v0.31.13 と本設計の構造的差異

v0.31.13 は `OVERRIDE_*` body を inline 展開して `fd.invLighting` / `fd.albedo` 等 lilToon body context を再生成しようとした → variant ごとの context 差で compile fail。

v0.34.0 は `LIL_SAMPLE_*` macro 1 点のみ介入。 lilToon の元 body は **そのまま走る**。 我々は sample primitive の戻り値を decode するだけ → variant 互換性 100% 構造保証。

#### TEXTURE2D 関数 parameter alias 層

architectural finding (2026-05-07): lilToon の 6 つの helper 関数 (`lilGetOutlineWidth`, `lilGetOutlineVector`, `lilCalcOutlinePosition*`, `lilGradationMap`, `lilParallax` / `lilPOM`, `lilCalcGlitter`) と 2 つの sub-texture 関数 (`lilGetSubTex`, `lilGetSubTexWithoutAnimation`) が **TEXTURE2D を関数パラメータとして受け取る**。

関数 body 内 `LIL_SAMPLE_2D_LOD(outlineWidthMask, samp, uv, 0)` は token paste で **小文字 param 名** ベース `_AR_LilSampleLOD_outlineWidthMask` に展開。 我々の specialization は **大文字 global property 名** ベース `_AR_LilSampleLOD__OutlineWidthMask` のみ。

mitigation: parameter forwarding alias 層を inject。 7 alias × 5 macro variant = 35 alias macro を emit:
```hlsl
#define _AR_LilSampleLOD_outlineWidthMask(samp, uv, lod) _AR_LilSampleLOD__OutlineWidthMask(samp, uv, lod)
// ...
```

#### 5 group toggle + 4 emergency disable + master switch

`AntiRippingTag` に新規 SerializeField 9 個追加 (default 全 OFF、 opt-in):
- `enableLilToonUniversalWrapper` (master、 緊急時 OFF で旧 OVERRIDE_* path に回帰)
- `encryptMaskGroup` / `encryptColorGroup` / `encryptNormalGroup` / `encryptEmissionGroup` (段階 release で activate)
- `disableMaskGroup` 等 emergency disable (build-time の 1-click partial rollback)

#### LilToonTextureCatalog 新規導入

`Editor/Pipeline/LilToonTextureCatalog.cs` (新規、 ~210 行) に lilToon `lil_common_input.hlsl:834-892` の 全 ~58 TEXTURE2D() prop を Category 分類で master list 化。 specialization emit の single source of truth。 `FunctionParameterAliases` も同 file に集約 (= alias mapping の source-of-truth)。

#### Shader-lock 失敗時の leak 防止 mitigation

shader 生成失敗 → BlendShape lock fallback path で、 暗号化対象 prop に対し `material.SetTexture(propName, null)` を spec 配列駆動で全実行。 visual collapse するが leak は 0 (= safety mode 相当)。 v0.31.13 で抜けていた防衛線を v0.34.0 で塞ぐ。

### Changed

`LilToonShaderInjector.BuildHookCode()` を mode 別に分岐:
- **ValueXor mode (default)**: universal wrapper path。 `BuildUniversalDecodeHelpers` + `BuildAllPerPropertySpecializations` + `BuildFunctionParameterAliases` + `BuildMacroRedefinitions`
- **XorSortMapping mode**: 既存 OVERRIDE_* path 温存 (= v0.31.x B+ の `_MainTex` 専用 mapping decode、 wrapper 化は v0.34.6+ で検討)

`TextureTargetSpec` に `Category` field 追加 (`LilToonTextureCatalog.Category` と整合)。 既存 3 entry に Color/Normal/Mask 値を埋め込み。

### v0.34.0 alpha 1 の検証 scope

- chiffon-spring avatar で従来 3 toggle ON、 build 成功
- Generated shader (`Assets/紫陽花広場/anti-ripping/Generated/Shaders/_AR_lts_*.shader`) の HLSLINCLUDE block に specialization が emit されている (3 個 decode + ~55 個 passthrough)
- Play mode で OSC unlock 前後で v0.33.9 と pixel-identical (regression なし)
- AssetRipper 抽出で 3 prop が依然 noise
- 4 lilToon variant (lilToon, Multi, Refraction, Outline) で個別 compile 確認

v0.34.1 以降で Mask group → Color group (face decal) → Normal group → Emission group を段階追加予定 (各段階で alpha → beta → stable cycle、 部分 rollback 可能設計)。

### Files Modified

- `Editor/Pipeline/LilToonTextureCatalog.cs` (新規、 +210 行)
- `Editor/Pipeline/TextureTargetSpec.cs` (Category field 追加)
- `Editor/Pipeline/TextureEncryptionPass.cs` (3 entry に Category 値設定)
- `Editor/Pipeline/LilToonShaderInjector.cs` (BuildHookCode rewrite、 +280 行 / -60 行)
- `Editor/Pipeline/ShaderLockPass.cs` (NullifyEncryptionTargetTextures + fallback path 強化)
- `Runtime/AntiRippingTag.cs` (9 SerializeField + 9 public property accessor 追加)

### Debug logger 強化 (`ARLog`)

新規 `Editor/ARLog.cs` (+70 行) で全 Anti-Ripping log を中央集約。 既存の `Debug.Log("[AntiRipping] ...")` 形式 (23 file × 72 call site) を `ARLog.Info("Tag", "...")` に migrate。

**形式**: <code>&lt;color=#9966FF&gt;[Anti-Ripping]&lt;/color&gt; [TAG] message</code>
- 紫青色の brand prefix で Unity Console 上で視認性が高い (rich text)
- 各 pass を識別する <code>[TAG]</code> (例: `Wrapper`, `ShaderInject`, `ShaderLock`, `TextureEnc`, `TextureEncFinalize`, `KeyAnim`, `MeshLock`, `Plugin`, `AutoKey`, `Cache`, `OSC`, `Watermark` 等)
- 純粋な事実のみ記録 (推測・「可能性がある」 等の不確実表現を排除)

**v0.34.0 wrapper 経路の新規 log**:
- `Wrapper`: locked shader 生成時に specialization 数 / alias 数 / macro variant 数を log
- `ShaderLock`: 各 material swap で renderer / slot / src shader / locked shader / mode を log (VerboseLogging 時)
- `ShaderLock` fallback: shader-lock 失敗時に null 化した暗号化対象 prop 数を log (leak 防止 mitigation の動作確認用)

---

## [0.33.9] — 2026-05-07 (lilToon カスタムシェーダーで material が pink になる致死バグ fix)

### Fixed

lilToon の **カスタムシェーダー機構** (= Inspector の「カスタムシェーダー作成」 や `.lilcontainer` / `.lilblock` / `custom.hlsl` を経由してサードパーティが生成した派生 shader、 例 BoundBonePro lilToonSquish) を使う material が、 VRCAAR ビルド後に **pink (shader compile error)** になる致死バグを修正。

#### 真因

`AntiRippingTag.ShouldLockShader` は shader.name に `"lilToon"` 部分一致で **無条件に shader-lock 対象** にする。 これにより lilToon カスタム派生 shader にも `LilToonShaderInjector` の injection が走るが、 ユーザー shader は (a) `.lilcontainer` 由来の独特な `#include` 構造、 (b) `custom.hlsl` 内のユーザー定義 `lilCustomVertexWS` と VRCAAR の `LIL_CUSTOM_VERTEX_OS` define の hook 相互作用、 (c) Outline pass 等の特殊構造で UV6/7 declare に整合性が取れない、 などの複合要因で **shader compile error** → material が pink になっていた。

再現 shader 名 例: `Hidden/BoundBonePro/lilToonSquish/Outline`。

#### 修正

副作用最小の hotfix。 injection 機構は **触らず**、 「injection が安全に通らない派生」 を **自動検出 + 手動 override** で shader-lock 対象から外し、 BlendShape lock + texture 非暗号化にフォールバック。

- **自動検出** (`LilToonShaderInjector.IsLikelyCustomLilToonDerivative`): shader.name に `"lilToon"` を含み、 かつ asset path が lilToon 公式 package (`jp.lilxyzw.liltoon`) 配下でない shader を自動的に skip。 公式 lilToon / lilToonMulti / lilToonRefraction / lilToonOnePassOutline は package 配下なので影響なし
- **手動 override** (`AntiRippingTag.excludeFromShaderLock`): Inspector の「Shader-Lock 除外名」 リストに shader 名の一部 (例: `BoundBonePro`) を追加すると、 自動検出で拾えない shader も skip 可能
- **shader-lock skip 後の保護**: SMR は BlendShape unlock で頂点復元 (mesh-level lock 維持)、 texture は元 srcTex がそのまま LAC に渡る (= 非暗号化、 ただし BlendShape lock で抽出 mesh は scramble 済み)

#### 副作用とトレードオフ

shader-lock skip された material は **shader-level vertex decode が動かず**、 SMR は BlendShape unlock のみで保護 (MR は永久 scrambled の既存仕様制約のまま)。 texture 暗号化も skip されるため、 抽出 PNG は元のまま見える。 lilToon カスタム shader は装飾的 material (decoration / VFX) で使われるケースが多いと想定し、 主 body material が標準 lilToon なら影響軽微。 主 body material がカスタムの場合は texture 露出のリスクが残る (= ユーザー判断)。

#### 検証手順

1. BoundBonePro Squish 入りプロジェクトで `enableShaderLevelDecode = true` のままビルド → material が pink にならない
2. Console に `[AntiRipping] shader 'Hidden/BoundBonePro/...' を shader-lock 対象から自動除外しました` の info ログが 1 行出る
3. ビルド成果物で `_AjisaiAR_Unlock` BlendShape を 100→0→100 で頂点復元確認
4. 標準 lilToon avatar で既存通り shader-lock + texture encryption が動作 (regression なし)
5. lilToon Refraction / Multi avatar で既存通り arlocked_ 経路が動作 (regression なし)

## [0.33.4] — 2026-05-06 (NDMF Manual Bake で texture 復号 shader が焼かれない致死バグ fix)

### Fixed

NDMF の `Tools → NDM Framework → Manual Bake Avatar` 経由でアバターを bake した結果、 `Texture Pixel Encryption` が **「暗号化されたまま表示される」** 状態になっていた致死バグを修正。 VRC SDK のアバターアップロードビルドでは正常動作していたため、 NDMF Manual Bake 経路でのみ起きていた。

#### 真因

NDMF の `AvatarProcessor.ManualProcessAvatar` (https://github.com/bdunderscore/ndmf `Editor/AvatarProcessor.cs:131-147`) は `ProcessAvatar(...)` 全体を `AssetDatabase.StartAssetEditing()` でラップしている。 一方 VRC SDK のアップロードビルドはこのラップが無い。

`StartAssetEditing` ブロック中は Unity 公式仕様 (https://docs.unity3d.com/ScriptReference/AssetDatabase.StartAssetEditing.html) として:

- `AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport)` でも **import が deferred** され、 shader compile は実行されない
- `AssetDatabase.Refresh()` も同様に deferred
- `Shader.Find(name)` は registry に未登録なので **null** を返す
- `AssetDatabase.LoadAssetAtPath<Shader>(path)` も asset 未登録のため **null** を返す

これにより `LilToonShaderInjector.GenerateLockedVariant` が null を返し、 `ShaderLockPass.SwapMaterialsToLocked` が **「locked variant 生成に失敗。 BlendShape lock にフォールバック」** に切り替わる。 `material.shader` は元の lilToon のまま残るが、 `TextureEncryptionPass` は予定通り `_MainTex` を暗号化済 byte で上書きしてしまうので、 shader 側に復号 hook が無い状態で **暗号化された noise texture がそのまま表示** される。

ユーザログでの具体的な証拠 (NDMF Manual Bake 時 3856 行のログ):
- 808-1678 行: `[AntiRipping] 生成 shader '...' を Shader.Find で取得できませんでした` × **31 連発**
- 同所: `[AntiRipping] 'Costume_Another' (shader=Hidden/lilToonTransparentOutline) の locked variant 生成に失敗。BlendShape lock にフォールバックします。`
- 3755 行以降: ようやく shader compile が開始されるが、 既に `ShaderLockPass` 終了後で手遅れ

VRC SDK アップロードビルドの 5675 行ログでは上記警告が **0 件** で、 shader-lock が正常完了していた。

#### 修正内容

新規ヘルパー `ShaderImportHelper.ResolveAfterImport(IList<string> paths, string findShaderName)` を `Editor/Pipeline/ShaderImportHelper.cs` に追加。 内部で 4 段の fallback chain を試行する:

1. **第 1 trial (通常路)**: `ImportAsset(p, ForceSynchronousImport)` を全 path に対して実行 → `Shader.Find(findShaderName)`
2. **第 2 trial (Refresh 路)**: 失敗なら `AssetDatabase.Refresh()` で偶発的な lag に対する保険
3. **第 3 trial (LoadAssetAtPath 路)**: registry 未登録でも path から直接 load を試行
4. **第 4 trial (Stop/Start dance 路, 新規)**: NDMF AssetEditing scope を一時解除して即時 compile を強制
   ```csharp
   try
   {
       AssetDatabase.StopAssetEditing();   // counter を 0 に下げて batch を flush
       ImportAll(paths);                    // 即時 compile
       resultShader = Shader.Find(findShaderName) ?? AssetDatabase.LoadAssetAtPath<Shader>(mainPath);
   }
   finally
   {
       AssetDatabase.StartAssetEditing();   // counter を復元 (NDMF の finally に Stop が残っている前提)
   }
   ```

VRC SDK アップロードビルド時は最初の `Shader.Find` で成功するので dance は走らない (no-op fallback)。 NDMF Manual Bake 時のみ第 4 trial に到達し、 shader が即時 compile される。

#### 修正対象ファイル

- 新規: `Editor/Pipeline/ShaderImportHelper.cs`
- 編集: `Editor/Pipeline/LilToonShaderInjector.cs` (`GenerateLockedVariant`, `GenerateSelfContainedLocked` の 2 箇所をヘルパーへ集約)
- 編集: `Editor/Pipeline/PoiyomiShaderInjector.cs` (`GenerateLockedVariant` をヘルパーへ集約)

#### 副作用と注意点

- **NDMF AssetEditing depth が 1 前提**: NDMF 公式 `AvatarProcessor.cs` は `try-finally` で 1 重なので OK。 他の MA / FaceEmo / lilycal-Inventory が更に nested で `StartAssetEditing` を呼んでいる場合は 1 回の stop では batch が解除されないので第 4 trial も null になり、 既存と同じ「BlendShape lock fallback」挙動に戻る (= **現状より悪化する事はない**)
- **per-shader stop/start のオーバーヘッド**: `s_Cache` が hit しなかったときだけ dance が走る。 アバターのユニーク shader 数 ≦ 数十程度なので許容範囲
- **VRC SDK build に対する変更**: 通常路 (第 1 trial) で成功するので挙動は完全に互換

#### 検証手順

1. `Tools → NDM Framework → Manual Bake Avatar` で同じアバターを bake
2. Console に「locked variant 生成に失敗」「Shader.Find で取得できませんでした」が **0 件** であること
3. Bake 結果のアバターの `Costume_*` / `Hair_*` SMR の material[0] が `AjisaiAR/Locked_<16hex>` を指していること
4. Play モード or VRChat 実機で texture が正常に復号されて元の見た目で表示されること
5. VRC SDK Build & Test も引き続き正常動作すること

## [0.33.3] — 2026-05-05 (UniversalRewrite mode 完全削除)

### Removed (clean-up)

`Encryption Mode` dropdown の `UniversalRewrite` を v0.32.10 で deprecated 警告へ降格していたが、 user 検証で改善が確認できないまま据え置かれていたため、 関連コードを完全撤去。 Inspector からも dropdown 自体が消え、 `LegacyOverride` (= v0.31.x 互換) 一本化となる。

#### コード削除内訳

- **enum / field / property** (Runtime/AntiRippingTag.cs):
  - `enum EncryptionMode { LegacyOverride, UniversalRewrite }` 削除
  - `[SerializeField] private EncryptionMode encryptionMode` 削除
  - `public EncryptionMode EncryptionMode => encryptionMode` 削除
  - `[SerializeField] private bool enableUniversalAutoDiscoverWrap` (v0.32.9 experimental gate) 削除
  - `public bool EnableUniversalAutoDiscoverWrap` 削除
- **Inspector** (Editor/AntiRippingTagEditor.cs):
  - `_encryptionMode` SerializedProperty 削除
  - 「実装方式 (v0.32)」LabelField + dropdown PropertyField + HelpBox 削除
  - 対象テクスチャ section の `DisabledScope(modeIdx == UniversalRewrite)` 削除
- **Pipeline ファイル削除**:
  - `Editor/Pipeline/EncryptionContext.cs` (+ .meta) — `Mode` / `EncryptedProperties` / `IsUniversalActive` 等を保持していた DTO
  - `Editor/Pipeline/HlslSourceRewriter.cs` (+ .meta) — `LIL_SAMPLE_2D[*]` → `_AR_DecodedSample(...)` regex 書換
  - `Editor/Pipeline/HlslIncludeWalker.cs` (+ .meta) — `#include` 再帰 walker
  - `Editor/Pipeline/LilToonIncludesPatcher.cs` (+ .meta) — lilToon Includes/ patched copy 生成
- **LilToonShaderInjector.cs**:
  - `EncryptionContext encContext` parameter を持つ public overload 削除 (= 元 `(Shader, LockedShaderMode)` 1 つのみに統一)
  - private method `ApplyUniversalRewriteIfEnabled` / `BuildUniversalHelperIfEnabled` 削除
  - `RewriteIncludePaths(content, sourcePath, patchedMapping)` overload 削除 → 単一 simplifies overload (= 相対 include を absolute path 化)
  - `BuildHookCode` の `universalActive` 分岐削除 (= 常に spec-driven OVERRIDE_*-per-spec を出力)
  - `InjectArkProperties` の universal property 宣言ループ削除
  - `TransformLtsPassContent` / `TransformLtsMainContent` / `GenerateSelfContainedLocked` / `InjectHookIntoHlslInclude` / `InjectHookIntoEachHlslProgram` から `EncryptionContext` parameter を全 削除
  - cache key から `encContextKey` component 削除 (= source shader 単位で 1 generated shader)
- **ShaderLockPass.cs**:
  - `BuildEncryptionContext(tag)` メソッド削除
  - `Run()` 内の auto-discover bulk register block / HitMissErrors log / IsUniversalActive log 削除
  - `ProcessRenderer` / `ProcessMeshRenderer` / `SwapMaterialsToLocked` の `EncryptionContext encContext` parameter 削除
  - `ApplyEncryptedTextures` / `ApplyTextureSeeds` の `tag.EncryptionMode == UniversalRewrite` 分岐削除 (= spec 配列駆動のみ)
- **TextureEncryptionPass.cs**:
  - `bool universalMode = ...` フラグと auto-discover encrypt block 削除
  - `s_AutoDiscoverDenyPrefixes` array + `IsAutoDiscoverDeny` メソッド削除
  - spec mask の RGBA 強制統一 (universal-only) 削除
- **AntiRippingPlugin.cs**:
  - `LilToonIncludesPatcher.ClearCache()` 呼出削除
  - dump log の `EncryptionMode={tag.EncryptionMode}` 行削除

### Maintained

- `LegacyOverride` 経路 (= v0.31.x 互換、 主要 3 prop OVERRIDE_*-per-spec) はそのまま — texture pixel encryption の default 動作。
- `s_StripPreserveList` (= visual fidelity 維持の preserve list) はそのまま。
- multi-material per-slot binding (v0.33.0 真因 fix) と元 v0.31.x uniform 名 `_AR_K0..3` / `_AR_TK0..3` (v0.33.2 復活) はそのまま。
- `CacheJanitor.SweepAllPatchedHlsl()` (= 旧 `_AR_inc_*.hlsl` の遺跡掃除) はそのまま — UniversalRewrite 時代の disk 残骸を build 開始時に削除する後処理として残す。

### Verification

1. `Build~/build.ps1` で v0.33.3 publish + ALCOM update
2. Unity 再起動 (= Editor/Pipeline/{EncryptionContext,HlslSourceRewriter,HlslIncludeWalker,LilToonIncludesPatcher}.cs が消えているので script reload)
3. AntiRippingTag Inspector を開き、 Step 3 の **「実装方式 (v0.32)」 + Encryption Mode dropdown が消えていること** を確認
4. NDMF Manual Bake → shader compile error 0 件 + 「multi-material avatar で texture decode 成功」 が v0.33.2 同等に動作すること

## [0.33.2] — 2026-05-05 (緊急 hotfix — v0.33.1 の per-material salt 撤回、 元 syntax 復活)

### Fixed (致死 regression: shader compile 50+ 分)

v0.33.1 の per-material instance unique uniform name 方式で **shader instance 数 = lockable material instance 数** に増加し、 multi-material avatar (= 10 slot 等) で shader compile 時間が **50+ 分** に。 typical avatar build (= 1-2 分) と比べて致死 regression。

#### 原因再評価

v0.33.0 で `[<i>]._<prop>` syntax に切替したが、 user 検証で AnimatorController runtime で動かないと判明。 v0.33.1 で per-material salt 化したが compile cost を underestimate。

しかし fact-check し直すと、 **Poiyomi RA / lilToon RA / VRCFury MaterialPropertyAction はすべて元 `material._<prop>` syntax で multi-material driving している実例**。 つまり Unity の `Renderer.material._<prop>` AnimationClip curve は **Renderer level で全 slot に setFloat を試みる仕様** (= documented されないが事実上のデファクト標準)。

つまり v0.33.0 の `[i]._<prop>` 切替自体が誤り (= 不要だった)。 元の v0.31.x `material._AR_K{i}` syntax で multi-material 全 slot 同期 driving が成立していた。

v0.31.x までユーザーが「multi-material は難読化できない」 と観察したのは、 旧 `SwapMaterialsToLockedAssetOnly` が `_AR_TK = keyBytes` 静的 pre-bake で leak していたため (= driving 不可と誤認、 実際は driving は機能していた)。 v0.33.0 の真因 fix (`SwapMaterialsToLockedAssetOnly` 撤去 + `GetSkipReason` 簡略化) は正しかったが、 binding syntax 変更は不要だった。

#### Reverted

- **v0.33.1 の per-material salt 化を全 撤回**:
  - `LockedRendererInfo.SlotSalts` 削除
  - `LilToonShaderInjector.GenerateLockedVariant(Shader, LockedShaderMode, EncryptionContext, string)` overload 削除
  - `PoiyomiShaderInjector.GenerateLockedVariant(Shader, string)` overload 削除
  - `ApplyMaterialSaltSuffix` post-process method 削除
  - cache key の salt 含有撤回 → 同 source shader で 1 generated shader (cache hit、 compile 時間 大幅短縮)
- **v0.33.0 の `[<i>]._<prop>` syntax を撤回**:
  - `BuildMaterialKeyClip` / `BuildMaterialKeyClipPerType` / `BuildMaterialTextureKeyClip` の binding を元 v0.31.x `material._AR_K{i}` / `material._AR_TK{i}` syntax に戻す
- **`SwapMaterialsToLocked` の SetFloat** を元 v0.31.x の `_AR_K0..3` / `_AR_TK0..3` (= salt 無し) に戻す

#### Maintained (= v0.33.0 で正しく fix した部分)

- `GetSkipReason` 簡略化 (= multi-material 特別扱い撤廃) → そのまま維持。 multi-material も `SwapMaterialsToLocked` で全 slot 処理。
- `SwapMaterialsToLockedAssetOnly` メソッド削除 (= 静的 pre-bake leak の root cause) → 維持。
- `arlocked_` prefix shader skip (= doubly-arlocked redefinition 防止) → 維持。

### v0.33.2 動作

| | v0.33.0 | v0.33.1 | v0.33.2 (本版) |
|---|---|---|---|
| multi-material processing | ✓ (全 slot Swap) | ✓ | ✓ |
| binding syntax | `[<i>]._<prop>` (= AnimatorController runtime で動かない) | `material._<prop>_<salt>` (= shader instance 増 で compile 50+ 分) | `material._<prop>` (= 元 syntax、 全 slot 伝播 + cache hit) |
| shader 数 | 1 / source shader | 1 / material instance (= 致死 cost) | 1 / source shader |
| compile 時間 | typical | **50+ 分** | typical (= 数分) |
| multi-material driving | 動作せず | 動作するが build cost 致死 | **動作する想定** (= Poiyomi/lilToon RA 同方式) |

### Verification (ユーザー側)

1. `Build~/build.ps1` で v0.33.2 publish + ALCOM update
2. `Generated/Shaders/_AR_*` を完全削除 (= v0.33.1 の大量 shader 残骸排除)
3. Unity 再起動
4. multi-material avatar で `EncryptionMode = LegacyOverride`、 NDMF Manual Bake
5. **shader compile 時間が typical (= 数分以内) に戻ること** ← 本 hotfix の primary 確認
6. shader compile error 0 件
7. VRChat 起動 → 解錠前: 全 slot で texture noise + mesh scrambled
8. **OSC unlock 後: 全 slot で texture decode 成功** ← multi-material driving 確認

### もし Step 8 で slot 1+ が依然復号されないなら

Unity の `material._<prop>` curve が事実上 slot 0 のみ伝播する Unity bug かもしれない。 その場合の確実な解決は **mesh 分割 (= per-slot SMR 化)** だが、 これは Performance Rank 影響 + AnimationClip 互換性 影響 + 実装 cost 大。 別 minor revision (v0.34) で慎重に再挑戦予定。

## [0.33.1] — 2026-05-05 (multi-material per-slot 動作 hotfix — per-material unique uniform 採用)

### Fixed

v0.33.0 で `[<slotIdx>]._<prop>` per-slot index syntax を採用したが、 ユーザー検証で「OSC unlock 後 multi-material の slot 1+ で texture が復号されない」 と判明。 prototype の register 確認 (= AnimationClip.SetCurve API) は PASS したが、 **AnimatorController runtime (= VRChat FX layer)** で `[i]._<prop>` curve が slot 1+ に setFloat されないことが実機で確証された。

#### 真因の真理 (= AnimatorController runtime の制約)

Unity 公式 doc の `[1]._Color.x` syntax は AnimationClip.SetCurve API で valid に register されるが、 **AnimatorController/Animator runtime の curve evaluator がこれを slot 0 のみに setFloat する**。 つまり register と runtime の動作が乖離している。 これは documented されておらず、 prototype の API レベル確認だけでは検出できなかった (= 5 連続失敗の reflection)。

#### 解決: per-material unique uniform name (Poiyomi/lilToon RA 同方式)

Poiyomi RA (Rename Animated) と lilToon RA は同じ問題を **shader-side で per-material unique uniform 名** で解決している (= 各 material instance に異なる uniform 名を持たせ、 AnimationClip は標準 `material._<unique_name>` syntax で driving)。 これを VRCAAR にも適用:

1. **`LockedRendererInfo.SlotSalts`** (List<string>) を新設。 LockedSlots と同 index で対応する 8 文字 hex salt を保持。
2. **`SwapMaterialsToLocked`** で各 slot に 8 文字 hex salt を生成し、 `LilToonShaderInjector.GenerateLockedVariant(..., materialSalt)` / `PoiyomiShaderInjector.GenerateLockedVariant(..., materialSalt)` に渡す。
3. **shader generation 末尾で post-process regex** で content 内 全 `_AR_K[0-3]` / `_AR_TK[0-3]` を `_AR_K{i}_<salt>` / `_AR_TK{i}_<salt>` に一括 rename (`\b` 境界で safe)。
4. **material clone の SetFloat** を `_AR_K{i}_<salt>` 等 salt 付き property 名に変更。
5. **`KeyAnimatorBuilder` の clip 生成 binding** を `material._AR_K{i}_<salt>` (= 既存安定 syntax + per-material unique suffix) に変更。 `[i]._<prop>` syntax を撤回。
6. cache key に salt を含め、 各 material instance に独立 generated shader を割り当て。

#### 動作原理

- AnimationClip curve `material._AR_K0_<saltA>` は **全 slot に setFloat を試みる** が、 該当 prop を持つ slot のみ反応 (= Unity の Renderer.SetFloat の internal 動作)
- slot 0 の material instance は uniform `_AR_K0_<saltA>` を declare → 該当 → setFloat 反映
- slot 1 の material instance は uniform `_AR_K0_<saltB>` を declare (= 別 salt) → 不該当 → 無視
- → per-slot 独立 driving 成立

これは Poiyomi RA / lilToon RA で実用稼働している方式で、 Unity の Animator runtime の subtle 動作に依存しない。

#### Files Modified

| File | 変更内容 |
|---|---|
| `Editor/Pipeline/ShaderLockPass.cs` | `LockedRendererInfo.SlotSalts` 追加、 `SwapMaterialsToLocked` overload 追加 + salt 生成、 clip binding 全 3 メソッドを `material._AR_K{i}_<salt>` syntax に変更 |
| `Editor/Pipeline/LilToonShaderInjector.cs` | `GenerateLockedVariant` に `materialSalt` overload 追加、 `ApplyMaterialSaltSuffix` post-process method 新設、 cache key + hash key に salt 含める |
| `Editor/Pipeline/PoiyomiShaderInjector.cs` | `GenerateLockedVariant` に `materialSalt` overload 追加 + post-process regex |
| `package.json` / `Build~/*.csproj` | v0.33.1 bump |

#### 副作用

- **AssetBundle size**: 各 material instance に独立 generated shader を割り当てるため、 shader 数 = lockable material instance 数 (典型 avatar で 5〜15 個)。 single-material avatar では従来通り source shader 1 個あたり 1 generated shader (cache hit)。
- **runtime cost**: per-material uniform は Unity 標準動作、 軽微。

#### Verification (ユーザー側で実行)

1. `Build~/build.ps1` で v0.33.1 publish + ALCOM update
2. 旧 `Generated/Shaders/_AR_*` を完全削除 + Unity 再起動
3. multi-material avatar で AntiRippingTag 設定 (`EncryptionMode = LegacyOverride` 推奨)
4. NDMF Manual Bake → shader compile error 0 件確認
5. VRChat 起動 → 解錠前: 全 slot で texture noise + mesh scrambled
6. **OSC unlock 後: 全 slot (= slot 0, 1, 2, ...) で texture decode 成功 ← 本 hotfix の判定基準**
7. AssetRipper で build 抽出 → 全 slot material が `_AR_K{i}_<salt>` / `_AR_TK{i}_<salt>` = 0 で焼かれている (= leak 防止確認)

### 新 prototype: Runtime Per-Slot Driving 検証 (継続提供)

`Tools/紫陽花広場/AntiRipping/[Prototype] Runtime Per-Slot Driving` で AnimatorController 経由の per-slot syntax 動作を実機検証可能 (= v0.33.0 の `[i]._<prop>` syntax が runtime で動かないことを示した検証手段)。

## [0.33.0] — 2026-05-05 (multi-material renderer 真因 fix — per-slot AnimationClip binding 採用)

### Fixed (本質的な multi-material 難読化失敗の真因)

ユーザー報告「**1 つの SkinnedMeshRenderer に複数のマテリアルがある場合、 難読化できない問題**」 の真因を特定して修正。

#### 真因 (fact-based 確定)

`ShaderLockPass.GetSkipReason` で `mats.Length > 1` の場合に `MultiMaterial` / `MultiMaterialAssetOnly` 経路に分岐し、 後者 (texture encryption ON 時) で `SwapMaterialsToLockedAssetOnly` が呼ばれ、 各 slot に **K=0 / TK=keyBytes 静的 pre-bake** していた。 つまり:

- `_AR_K = 0` (vertex displacement key) → mesh は decoded 状態で固定 (BlendShape lock で別途 scramble)
- **`_AR_TK = 正解 keyBytes`** (texture decode key) → texture は **常に decoded 状態で焼かれる**

→ build asset 抽出時に material asset を見ると `_AR_TK0..3` に正解 key が露出 → attacker が直接 decode 可能 → **texture 完全 leak**。

加えて `KeyAnimatorBuilder` の binding は `material._AR_K{i}` syntax (= slot 0 のみ driving) で、 multi-material renderer の slot 1+ には `_AR_K` を runtime driving する手段が無いと判断されていた (line 1203-1205 のコメント自体が認めていた)。

#### 解決の根拠

Unity 公式 [AnimationClip.SetCurve doc](https://docs.unity3d.com/ScriptReference/AnimationClip.SetCurve.html) に明記:
> A specific Material, on a renderer with multiple materials, can be referenced as **"[1]._Color.x"** (for the second material).

つまり `propertyName = "[<slotIdx>]._<prop>"` で **per-slot Float curve driving が公式サポート** されていた (Unity 2020.3.25f1+ で過去 bug fix 済み、 Unity 2022.3 で確実動作)。 VRCAAR は Unity 2022.3 専用なので、 これを採用すれば multi-material renderer の **各 slot 独立で `_AR_K0..3` / `_AR_TK0..3` を runtime driving 可能**。

#### Changes (実装)

1. **`ShaderLockPass.GetSkipReason` 簡略化** (`Editor/Pipeline/ShaderLockPass.cs:335`): `mats.Length > 1` 分岐を撤廃。 1 つでも lockable shader を持つ material slot があれば `SwapMaterialsToLocked` で全 slot を walk + 個別 inject。

2. **`SkipReason.MultiMaterial` / `MultiMaterialAssetOnly` enum 値削除**: 特別扱いが不要になった。

3. **`SwapMaterialsToLockedAssetOnly` メソッド削除**: 静的 pre-bake (= `_AR_TK = keyBytes` 焼き) は leak の root cause。 全 slot 通常 path で処理に統一。

4. **`KeyAnimatorBuilder` の binding を per-slot index syntax に変更** (`BuildMaterialKeyClip` / `BuildMaterialKeyClipPerType` / `BuildMaterialTextureKeyClip`):
   - 旧: `propertyName = "material._AR_K{i}"` (= slot 0 のみ)
   - 新: `propertyName = "[{slotIdx}]._AR_K{i}"` (= per-slot)
   - `LockedRendererInfo.LockedSlots` を loop して各 slot 独立 binding を生成

5. **prototype 検証 menuitem 追加** (`Editor/Diagnostics/PerSlotBindingPrototype.cs`): user が Unity で 5 秒で per-slot binding syntax の動作を確認可能。 `Tools/紫陽花広場/AntiRipping/[Prototype] Verify Per-Slot Binding`。

#### 期待する成果

| | v0.32.10 (前版) | v0.33.0 (本版) |
|---|---|---|
| single-material renderer の難読化 | ✓ | ✓ (動作維持) |
| multi-material renderer の **mesh** lock | ✓ (BlendShape) | ✓ (BlendShape) |
| multi-material renderer の **texture** lock | ✗ (静的 pre-bake で leak) | ✓ (per-slot Animator driving、 build asset では `_AR_TK = 0`) |
| build asset 抽出時の texture leak | ✗ (multi-material は decoded byte 焼き) | ✓ (全 slot で encrypted byte 維持) |

#### Verification (ユーザー側で実行)

**Step 1: prototype 検証 (= 5 秒、 致命的)**

1. Unity で v0.33.0 ALCOM update
2. Hierarchy で multi-material SMR (= 2 slot 以上) を選択
3. メニュー `Tools / 紫陽花広場 / AntiRipping / [Prototype] Verify Per-Slot Binding` を実行
4. Console log を確認:
   - `✓ PASS: per-slot binding N 件全て register 成功` → **本実装も正常動作する見込み**、 Step 2 へ
   - `✗ FAIL: ...` → Unity 仕様で syntax が reject されている → **本実装は機能しない**、 user 報告で plan 全体を再検討

**Step 2: e2e 検証 (本実装)**

1. multi-material avatar (= 顔 + 服 + 装飾 等) で AntiRipping 設定:
   - `EncryptionMode = LegacyOverride` (= v0.31.15 互換、 確実動作)
   - `EnableTexturePixelEncryption = true`
   - 主要 toggle (`EncryptMainTex` / `EncryptNormalMap` / `EncryptAlphaMask`) を ON
2. NDMF Manual Bake → shader compile error 0 件
3. build asset (= `Library/PackageCache/...` の vrchat_avatar_*) を AssetRipper で抽出 → 全 slot の material が `_AR_K0..3 = 0` / `_AR_TK0..3 = 0` で焼かれている (= leak 防止確認)
4. VRChat 起動 → 解錠前: 全 slot で texture noise + mesh scrambled
5. OSC unlock → 全 slot で texture decode + mesh restored

### 既知の限界

- **UniversalRewrite mode**: v0.32.10 で deprecated 状態のまま。 LegacyOverride mode を引き続き推奨。 multi-material 修正と独立。
- **AnimationClip curve 数**: multi-material 4 slot avatar = (8 K + 4 TK) × 4 slot = 48 curves/SMR。 single-material は 12 curves/SMR。 約 4 倍だが Animator runtime cost は線形で軽微。
- **AAO TraceAndOptimize 互換性**: `[<slotIdx>]._<prop>` syntax が AAO の property 解析で正しく扱われるかは未検証。 もし AAO で「未使用」 と誤判定されて削除される場合は、 AntiRippingTagAAOInformation で `[<i>]._AR_K*` / `[<i>]._AR_TK*` を preserve list に追加する後続 task が必要。

### Notes

- 5 連続 hotfix (v0.32.5〜v0.32.10) で UniversalRewrite mode の真因究明に時間を浪費させた reflection から、 v0.33.0 では **prototype 検証を最優先** (= Phase 4 で `[<slotIdx>]._<prop>` syntax の実機動作を確認可能な menuitem を提供) した。 deploy 前に user が 5 秒で「機能するか」 を確認できる safety net。

## [0.32.10] — 2026-05-05 (撤退 — UniversalRewrite mode を v0.32.7 等価まで rollback、 LegacyOverride 推奨)

### 重要 (透明な実装失敗報告)

v0.32.0〜v0.32.9 にわたり開発した **UniversalRewrite mode** (= lilToon の全 hlsl を patched copy 化して universal な texture decode を提供) は、 **致死 shader compile error が直せず** 実装失敗状態に陥った。 5 連続 hotfix (v0.32.5〜v0.32.9) でも shader compile error が消えず、 ユーザーから「同じ不具合が直っていない」 と報告 (= 5 回連続失敗認識)。

**真因 (確定したが解決不能)**: walker scope 拡張 (v0.32.8 修正 A) で patched lil_*.hlsl を生成すると、 ltspass shader の `#include` path が relative (`Packages/jp.lilxyzw.liltoon/Shader/Includes/`) から absolute (`Assets/紫陽花広場/anti-ripping/Generated/Shaders/`) に書換される。 patched 版と元 file の content は完全に一致するが、 Unity shader compiler の internal context (preprocessor define / search path priority) が relative include 経路と異なり、 lilToon の legacy DX9 path / modern HLSL 5+ path 整合 が破壊される。 結果、 `tex2Dlod`: no matching 2 parameter intrinsic function (`lil_pass_forward_normal.hlsl(279)` 等の OVERRIDE_MAIN call site で発生)、 lilToonRefraction で doubly-arlocked redefinition、 等の致死 compile error。

短期解決には **Unity shader compiler の internal 動作の deep 解析 + lilToon include 構造の特殊対応** が必要で、 minor revision の scope を超える。 ユーザー被害を最小化するため、 v0.32.10 で **v0.32.7 等価まで撤退** し UniversalRewrite mode を deprecated 扱いとした。

### Reverted

#### 修正 A 撤回 (walker scope を single-root に戻す)

**File**: `Editor/Pipeline/LilToonShaderInjector.cs:130-148`

`LilToonShaderInjector.GenerateLockedVariant` で:
```csharp
// v0.32.8/9: walker root に lts.shader + 全 ltspass.shader を渡す (= multi-root)
var allRoots = new List<string>(passRefs.Count + 1) { srcPath };
foreach (var passName in passRefs) { ... allRoots.Add(...); }
patchedIncludeMapping = LilToonIncludesPatcher.GetOrPatchIncludes(allRoots, encContext);
```

を v0.32.7 等価:
```csharp
patchedIncludeMapping = LilToonIncludesPatcher.GetOrPatchIncludes(srcPath, encContext);
```

に戻した (= single-root walker)。 `LilToonIncludesPatcher.GetOrPatchIncludes(IList<string>, ...)` overload は将来再挑戦のため残存。

### Fixed

#### doubly-arlocked redefinition (構造的防止)

**File**: `Editor/Pipeline/LilToonShaderInjector.cs:GenerateLockedVariant`

`source.name` が `arlocked_` prefix を含む shader (= 既に injection 済) を入力された場合、 source 自身を返して再 inject を skip。 v0.32.9 の `ExtractUsePassReferences` 内 skip と併せて 2 段防御。 これで `Hidden/ltspass_arlocked_ltspass_arlocked_lilToonRefraction_*` のような重複 inject は構造的に発生しない。

### Changed

#### UniversalRewrite mode に明示的 deprecated warning

**File**: `Editor/Pipeline/ShaderLockPass.cs:BuildEncryptionContext`、 `Runtime/AntiRippingTag.cs` の Tooltip

UniversalRewrite mode を選択した build で `Debug.LogWarning` を出力 (= 「実装失敗状態、 LegacyOverride 推奨」)。 Inspector の `EncryptionMode` field の Tooltip も update。

### v0.32.10 動作

| 機能 | LegacyOverride mode (推奨) | UniversalRewrite mode (deprecated) |
|---|---|---|
| 主要 3 prop 復号 (`_MainTex` / `_BumpMap` / `_AlphaMask`) | ✓ (= v0.31.15 等価、 確実) | ✗ (walker scope 撤回で patched 版が無く wrap 走らず) |
| auto-discover prop 復号 | ✗ (auto-discover 自体しない) | ✗ (gate off で wrap 対象外) |
| asset 上 texture 暗号化保護 | 主要 3 のみ | 主要 3 + 多数の prop (~17 件、 暗号化はされるが復号されないため runtime noise) |
| visual fidelity | ✓ 完全維持 (preserve list で部分 leak 許容) | ✗ runtime で多数 texture が noise 表示 |
| shader compile error | ✗ (= 無し) | ✗ (= 無し) |

### ユーザー向け対応 (重要)

1. **AntiRippingTag Inspector** で `EncryptionMode` を **`LegacyOverride`** に切替 (= v0.31.15 互換動作、 visual fidelity 完全 + 主要 3 prop 確実復号)
2. `Generated/Shaders/_AR_*` を **手動完全削除** + Unity 再起動 (= v0.32.x の stale cache 完全排除)
3. NDMF Manual Bake 実行
4. VRChat 内で OSC unlock → 主要 3 prop が正しく復号されることを確認

### v0.32.x 試行の経緯と教訓

- **v0.32.0〜0.32.4** (Phase 1〜2 alpha): UniversalRewrite mode 基本実装 (rewriter / walker / patched copy 生成)
- **v0.32.5** (hotfix #1): UnityCG.cginc 等の Unity built-in include 保持 (重要 fix、 これ自体は正しい)
- **v0.32.6** (hotfix #2): RGBA mask 統一 + `_Dither` deny (これ自体は妥当)
- **v0.32.7** (hotfix #5): asuint → (uint) cast (重要 fix、 これ自体は正しい)
- **v0.32.8** (修正 A): walker scope 拡張 ← ここで shader compile error 発生
- **v0.32.9** (修正 A 維持 + auto-discover gate off): error 同じ → 真因は 修正 A と確定
- **v0.32.10** (本版): 修正 A 撤回 = v0.32.7 等価に戻る

教訓:
1. lilToon × Unity shader compiler の internal 動作 (relative vs absolute include path で context が変わる) は documented されず、 静的に検証困難。 patched copy 戦略を取る前に **小規模 prototype で end-to-end shader compile を確認** すべきだった。
2. 5 回連続 hotfix で同じ症状が直らなかった時点で「真因 hypothesis 自体が誤り」 を疑うべきだった。 確認バイアスに陥らず、 早めに撤退すべきだった。
3. ユーザー時間の浪費 (= 各 deploy で Unity 再起動 + Generated/ 削除 + Manual Bake + VRChat 起動の検証 cycle) を 5 回繰り返させてしまった。

### Roadmap

- **v0.32.x maintenance**: LegacyOverride 互換 (= v0.31.15 動作) を確実 deliver。 UniversalRewrite mode は warning のみで実質的に no-op。
- **v0.33+ で再挑戦**: lilToon shader 構造の deep 改造 (= patched copy を Packages/ 形式 location に置く / Unity の relative include resolver の動作を完全再現する custom resolver 実装) を検討。 minor revision で慎重に実装。

## [0.32.9] — 2026-05-05 (緊急 hotfix — auto-discover wrap を gate off + doubly-arlocked fix)

### Fixed (致死 shader compile error)

v0.32.8 deploy 後、 ユーザー報告で 2 系統の shader compile error が判明:

#### 1. `tex2Dlod`: no matching 2 parameter intrinsic function (3 件)

`Hidden/ltspass_arlocked_ltspass_*` shader の compile で:
- `lil_pass_forward_normal.hlsl(279)` (= OVERRIDE_MAIN call site, Forward pass)
- `lil_pass_meta.hlsl(67)` (= OVERRIDE_MAIN call site, META pass)
- `lil_common_frag_alpha.hlsl(32)` (= OVERRIDE_MAIN call site, SHADOW_CASTER pass)

**真因**: v0.32.8 修正 B で encContext.EncryptedProperties に auto-discover prop ~17 件 (`_MatCapTex` / `_EmissionMap` / `_OutlineTex` 等) が追加された結果、 patched `lil_common_frag.hlsl` 等で **大量の `LIL_SAMPLE_2D[*]` 呼出が `_AR_DecodedSample(...)` で wrap** された。 lilToon の variant matrix (NORMAL/LITE/MULTI × FORWARD/META/SHADOW_CASTER) のどこかで legacy (DX9 path) と modern (HLSL 5+ path) の整合が破壊され、 `tex2Dlod(Texture2D, float4)` の引数 type mismatch が発生。

修正 (= 段階的解決):
- **`AntiRippingTag.EnableUniversalAutoDiscoverWrap` (default OFF) gate** を新設し、 修正 B (auto-discover bulk register) を gate 越しに適用するよう変更。 default OFF で v0.32.7 と等価な shader compile path を保つ。 主要 3 prop (`_MainTex` / `_BumpMap` / `_AlphaMask`) のみ wrap → patched lil_common_frag.hlsl 内で 3 wrap output になり、 元 lilToon と近い shader compile path → compile error 消失。
- **副作用**: auto-discover で暗号化された property (例 `_EmissionMap`) は CPU 側 noise 化されるが shader 側で wrap 経路が無いため、 VRChat 内で対応 texture は noise 表示。 ただし **asset 上は暗号化保護維持** (= AssetRipper 抽出で得られるのは encrypted noise PNG)。
- v0.32.x の以降の minor で variant matrix 対応した auto-discover wrap を再有効化予定。

#### 2. `redefinition of '_AR_K0' at line 963` (lilToonRefraction で doubly-arlocked)

`Hidden/ltspass_arlocked_ltspass_arlocked_lilToonRefraction_<hash>_<hash>` という **doubly-arlocked** shader name で `_AR_K0` の重複 declare error。

**真因**: lts.shader が UsePass で **既に inject 済の `arlocked_lilToonRefraction_*` shader** を referencing しているケースで、 NDMF pipeline の重複呼出 / cache 不整合などで 2 回目 inject が走り、 `_AR_K0` を 2 重 declare → redefinition error。

修正:
- `LilToonShaderInjector.ExtractUsePassReferences` で **`arlocked_` prefix を含む shader 名を skip**。 既に inject 済の ltspass を再 inject しないことで重複 declare を構造的に防ぐ。

### Changed (cache 管理強化)

- `CacheJanitor.StaleThresholdDays` を 7 → 1 day に短縮 (= 旧 cache 残骸が次 build で reuse されない)。
- 新メソッド `CacheJanitor.SweepAllPatchedHlsl()` を追加し、 build 開始時に `_AR_inc_*.hlsl` を **unconditional 削除**。 hash 変化で disk に残る遺跡 file の log noise を構造的に解消。 当 build で必要な patched .hlsl は `LilToonIncludesPatcher.GetOrPatchIncludes` が再生成する (cache key で再 hit すれば skip)。
- `AntiRippingPlugin.Apply` で build 開始時に `SweepAllPatchedHlsl()` を呼出。

### v0.32.7 → v0.32.9 の効果

| 機能 | v0.32.7 | v0.32.8 | v0.32.9 (本版) |
|---|---|---|---|
| 主要 3 prop の shader 復号 (= `_MainTex` / `_BumpMap` / `_AlphaMask`) | ✗ (walker scope 不足で wrap されない) | ✓ (walker scope 拡張で wrap) | ✓ |
| auto-discover prop 復号 (= `_EmissionMap` 等) | ✗ (encContext 未登録) | ✓ (登録) | ✗ (gate off、 noise 表示) |
| shader compile error | ✗ (= material error 無し) | ✓ (= 致死 error) | ✗ (= material error 無し) |
| asset 上の texture 暗号化保護 | ✓ (主要 3 のみ) | ✓ (主要 3 + auto-discover) | ✓ (主要 3 + auto-discover、 ただし auto-discover は復号できないため runtime noise) |
| doubly-arlocked redefinition | (未観測) | ✗ (致死) | ✓ (構造的防止) |

**本 version の中間立場**: 主要 3 prop の shader 復号成功 (= v0.32.7 から walker scope 修正分は前進)、 auto-discover prop は asset 上保護されるが runtime で noise (= 部分復元状態)。 完全復元は次 minor で variant matrix 対応 (= legacy/modern path mismatch の真因究明 + 個別修正) 後に再有効化。

## [0.32.8] — 2026-05-05 (Phase 2 真因 fix — walker scope 拡張 + auto-discover register)

### Fixed

v0.32.4-7 を経ても **テクスチャ復元が失敗する** 状態が継続していた真因を 2 つ特定して修正。

#### Bug A: `LilToonIncludesPatcher` の walker scope 不足

`LilToonShaderInjector.GenerateLockedVariant` から `LilToonIncludesPatcher.GetOrPatchIncludes(srcPath, encContext)` が `srcPath = lts.shader path` 単独で呼ばれていた。 lilToon は 2-tier 構造 (lts.shader: HLSLPROGRAM 限定的 / ltspass_*.shader: 各 Pass に HLSLPROGRAM 6 個) で、 真の sample 呼出 (`LIL_SAMPLE_2D_ST(_EmissionMap, ...)`) は ltspass が `#include` する `lil_pass_forward.hlsl` → transitive `lil_common_frag.hlsl` 内にある。 lts 単独を root にすると walker が `lil_common_frag.hlsl` 等に到達できず、 `HlslSourceRewriter` の wrap が走らなかった。 patched 集合に `lil_common_frag.hlsl` / `lil_pass_forward.hlsl` / `lil_pass_shadowcaster.hlsl` / `lil_pass_meta.hlsl` が含まれず、 ltspass shader の `#include "lil_pass_forward.hlsl"` が `RewriteIncludePaths` で **元ファイルへの absolute path に書換** され、 元の非-patched HLSL が compile される → wrap されない sample 呼出が runtime で実行 → `_AR_DecodedSample` が呼ばれず暗号化生 byte が表示。

**修正**: `LilToonIncludesPatcher.GetOrPatchIncludes` に `IList<string>` を受ける multi-root overload を追加し、 `LilToonShaderInjector.GenerateLockedVariant` で `passRefs` から resolve した全 ltspass path + lts.shader path を root として渡す。 `HlslIncludeWalker.Walk` は visited set を共有するため、 順次呼ぶだけで union of reachable が正しく計算される。 cache key prefix を `phase2|` → `phase2-multi|` に変更し、 旧 single-root cache の誤 hit を防ぐ (= 旧 patched .hlsl は touch されず CacheJanitor が 7 日後に自然 sweep)。

#### Bug B: `BuildEncryptionContext` の auto-discover prop 漏れ

`ShaderLockPass.BuildEncryptionContext` (line 1177) は `s_Targets` 配列の 3 spec (`_MainTex` / `_BumpMap` / `_AlphaMask`) しか `encContext.EncryptedProperties` に登録していなかった。 一方 `TextureEncryptionPass.ProcessRenderer` の universal mode auto-discover (v0.32.4 で追加) は広範な property (`_EmissionMap` / `_MatCapTex` / `_RimColorTex` / `_OutlineTex` / `_DetailMask` 等) を encrypt するが、 結果は `texEncResult.SeedMap` に残るだけで `encContext.EncryptedProperties` には反映されない。 `HlslSourceRewriter.Rewrite` は `encryptedProperties.Contains(args[0])` で wrap 判定するため、 auto-discover prop の sample 呼出は **無条件で touch されないまま** patched copy に残る。 加えて `LilToonShaderInjector` の per-property seed declaration loop (line 516, 667) も `encContext.EncryptedProperties` を回すので、 auto-discover prop の `_AR_<Tex>SeedLo/Hi/Linearize` 宣言が生成されない (= 仮に手で wrap しても compile error)。 `ShaderLockPass.cs:1175` のコメント自体が「Phase 3 で auto-discovery 拡張予定」 と未実装を認めていた。

**修正**: `ShaderLockPass.Run` で `BuildEncryptionContext` 直後・ renderer walk 前に `texEncResult.SeedMap.Keys` から `propertyName` を抽出して `encContext.EncryptedProperties` に bulk add + `RecomputeCacheKey()`。 これで auto-discover で encrypt された property も rewriter の wrap 対象に登録され、 `_AR_<Tex>SeedLo/Hi/Linearize` declaration も自動生成される。

#### 2 つの bug の関係

| ユーザー観察 | A 単独修正 | B 単独修正 | A+B 併修 (本修正) |
|---|---|---|---|
| 主要 3 prop (`_MainTex` 等) | ✗ 復元失敗 | ✓ 復元成功 | ✓ 復元成功 |
| auto-discover prop (`_EmissionMap` 等) | ✗ 復元失敗 | ✗ 復元失敗 (encContext 内でも walker reach しない) | ✓ 復元成功 |

ユーザーの「全 texture が暗号化されたが復元失敗」 観察は両 bug 併発による。 単独修正では部分復元に留まり regression に見えるため、 両者同時 commit。

### Files changed

- `Editor/Pipeline/LilToonIncludesPatcher.cs`: 新 multi-root overload (`IList<string>`)、 既存 single-root を thin-wrap、 cache key prefix 変更
- `Editor/Pipeline/LilToonShaderInjector.cs`:
  - `GenerateLockedVariant`: passRefs から ltspass path resolve + multi-root 呼出
  - `GenerateSelfContainedLocked`: API 統一 (single root を IList で渡す)
- `Editor/Pipeline/ShaderLockPass.cs`: `Run` 内で texEncResult.SeedMap から bulk register

### Verification (修正完了の判定基準)

1. `Generated/Shaders/_AR_inc_<hash>_*.hlsl` に `lil_common_frag.hlsl` / `lil_pass_forward.hlsl` / `lil_pass_shadowcaster.hlsl` / `lil_pass_meta.hlsl` が含まれる (= Bug A 修正確認)
2. build report の `encrypted properties = ...` 行に主要 3 + auto-discover prop が列挙される (= Bug B 修正確認)
3. patched `lil_common_frag.hlsl` 内に `_AR_DecodedSample(LIL_SAMPLE_2D_ST(_EmissionMap, ...), ..., (uint)(_AR_EmissionMapSeedLo), ...)` の wrap が現れる (= 統合確認)
4. Console に `[HitMiss]` warning 0 件 (= silent leak ゼロ)
5. `_AR_ltspass_*.shader` 内の `#include "lil_pass_forward.hlsl"` が `_AR_inc_<hash>_lil_pass_forward.hlsl` に redirect される
6. VRChat で OSC unlock → 全 texture 復元

## [0.32.7] — 2026-05-05 (Phase 2 致死 bug fix — asuint vs (uint) cast)

### Fixed

- **テクスチャ decode が完全失敗する致死 bug**: `HlslSourceRewriter.Rewrite` が `_AR_DecodedSample` 呼出を生成する際、 seed parameter を **`asuint(_AR_FooSeedLo)`** で渡していた。
  - HLSL `asuint(float)` は **bit reinterpretation** (IEEE 754 float bit pattern を uint として読む)。
  - `material.SetFloat("_AR_MainTexSeedLo", 22136.0f)` で bake された seed (= float 22136.0) を `asuint` すると `0x46AC1C00 = 1186930688` (= IEEE 754 bit pattern) となり、 値 22136 にならない。
  - `_AR_DecodedSample` 関数内で `stored = (seedHi << 16) | seedLo` が garbage 値 → `effSeed = stored ^ kp` も garbage → mask 計算が完全に間違う → **全 channel decode 失敗 → texture が encrypted noise のまま表示**。
  - 既存 OVERRIDE_*-per-spec body (`LilToonShaderInjector.cs:860` 等) と `BuildVertexHookAndCommonProperties` の `(uint)_AR_TK0` cast は **`(uint)`** (= 値変換) を使っており正常動作していた。 私が新設した HlslSourceRewriter だけが `asuint` を使用していたミス。
- **修正**: `HlslSourceRewriter` で `asuint(seedLoProp)` → `(uint)(seedLoProp)` に変更 (= 値変換、 truncate float → uint)。 既存 OVERRIDE_*-per-spec の cast semantic と一致。
- **顕在化 timing**: Phase 1 (v0.32.0-3) では rewriter が lts/ltspass 本体だけ walk し、 lilToon Includes/ を patch しなかった → `_AR_DecodedSample` がほぼ呼ばれず asuint bug が dormant。 Phase 2 (v0.32.4-6) で patched Includes/ が `_AR_DecodedSample` を実呼出するようになり即顕在化。

## [0.32.6] — 2026-05-05 (Phase 2 alpha hotfix #2 — encrypt/decode mask 整合)

### Fixed

- **VRChat 内で OSC unlock 後 mesh 不可視になる致死 bug** (v0.32.5 観測): 真因は **暗号化 mask と decode mask の不整合**。
  - `_MainTex` の TextureTargetSpec は `Mask = ChannelMask.RGB` (alpha 保護) で、 LegacyOverride mode の OVERRIDE_MAIN body の `_AR_DecodePixelRGB` (RGB only decode) と整合していた。
  - Phase 2 universal mode で **patched lilToon Includes/** の shadow / outline / meta pass の `LIL_SAMPLE_2D_ST(_MainTex, ...)` が rewriter で `_AR_DecodedSample(...)` (= **RGBA 4 channel decode**) に wrap される。 RGB 暗号化 + RGBA decode で alpha が `origA XOR maskA = wrong` になり、 shadow pass の `clip(decoded.a - _Cutoff)` で **全 pixel discard** → mesh 不可視。
  - `_DitherTex` は `lilSamplePointRepeat` 経由の **直接 `tex2D()` 呼出** で sample されるため rewriter が wrap しない → 暗号化されると dither pattern 破綻 → 全 pixel discard の risk もあった。

### Changes

1. **Universal mode で spec 暗号化 mask を `ChannelMask.RGBA` に強制統一**: encryption と `_AR_DecodedSample` の RGBA decode を整合させる。 `_MainTex` (RGB → RGBA) / `_AlphaMask` (ROnly → RGBA) を universal mode のみ override。 LegacyOverride mode は spec のまま (= v0.31.x 互換)。
2. **Universal mode で OVERRIDE_*-per-spec emission を skip**: Phase 2 で patched Includes/ が全 sample 呼出を `_AR_DecodedSample` で wrap するため redundant。 OVERRIDE_*-per-spec の RGB decode と encryption の RGBA mask の不整合 risk を構造的に回避。 LegacyOverride mode は従来通り emission (= v0.31.x 互換)。
3. **`_Dither` prefix を deny list に追加**: `_DitherTex` / `_DitherMaskLOD` が `tex2D()` 直接 sample のため rewriter 対象外。 暗号化すると dither 破綻 → cutout / shadow で全 pixel discard。

### Architecture (v0.32.6 universal mode 確定)

- 全 Texture2D properties (auto-discovered + spec) を **RGBA mask で encryption**
- patched lilToon Includes/ の全 `LIL_SAMPLE_2D[*]` 呼出が `_AR_DecodedSample(..., RGBA decode)` に wrap される
- OVERRIDE_*-per-spec emission **無し** (Phase 1 では emission したが Phase 2 では redundant + 不整合 risk)
- `_DitherTex` / `_DitherMaskLOD` 等は deny list で encryption 除外 → dither pattern 維持

## [0.32.5] — 2026-05-05 (Phase 2 alpha hotfix #1)

### Fixed

- **マテリアルエラー (= shader compile fail) の致死 bug**: `LilToonIncludesPatcher.RedirectIncludePaths` と `LilToonShaderInjector.RewriteIncludePaths` で、 lilToon の patched .hlsl が `#include "UnityCG.cginc"` 等の **Unity built-in cginc** (CGIncludes/ にある) を相対参照していたが、 sourceDir + filename で誤って `Packages/jp.lilxyzw.liltoon/Shader/Includes/UnityCG.cginc` のような **存在しない絶対 path** に rewrite していた → `Couldn't open include file` で全 patched shader が compile fail。
- **修正**: 相対 include を絶対 path 化する前に `File.Exists(resolved)` で **disk 上に存在するか check**。 存在しない (= UnityCG.cginc / AutoLight.cginc / Lighting.cginc / UnityMetaPass.cginc 等の Unity built-in) ならば original include を保持し、 Unity の CGIncludes search path に解決を任せる。

## [0.32.4] — 2026-05-05 (Phase 2 alpha — universal full coverage)

### Added (Phase 2: ~50+ properties に encryption 拡張)

- **TextureEncryptionPass.ProcessRenderer** に **auto-discover** を追加: universal mode のとき material の全 Texture2D property を walk + deny prefix filter (`_Audio*` / `_Camera*` / `_Grab*` / `_Udon*` / `_VRChat_*` / `_lilBackground` / `_AR_*`) で除外 + `is Texture2D` cast で RT/CRT/Cubemap/3D/2DArray 自動 skip + `Packages/` 配下 (lilToon ramp / Unity 内蔵) skip → 残り全 properties を encrypt。
- **`LilToonIncludesPatcher`** 新設: lilToon `Includes/*.hlsl` を recursive walk + patched copy 生成 (= rewriter 適用 + transitive include path redirect)。 universal mode で全 sample call 経路に decode 注入。
- **`LilToonShaderInjector.RewriteIncludePaths`** に patched mapping 引数追加: lts/ltspass の `#include` を patched 版に redirect。
- **`ShaderLockPass.ApplyEncryptedTextures` / `ApplyTextureSeeds`** を universal mode で auto-discovered properties にも適用。 `ApplyLinearizeForProperty` を新設 (sRGB flag を `_AR<Tex>Linearize` に bake)。
- AntiRippingPlugin.Apply で `LilToonIncludesPatcher.ClearCache()` を build start に呼び stale path mapping を防止。

### Phase 2 動作

- **LegacyOverride mode**: v0.31.15 完全互換 (regression なし)
- **UniversalRewrite mode**:
  - 3 spec properties (`_MainTex` / `_BumpMap` / `_AlphaMask`): 従来通り OVERRIDE_*-per-spec body で decode
  - 残り auto-discovered properties (~50: emission / matcap / outline / rim / 2nd / shadow tinting / detail / glitter / fur 等): **patched lilToon Includes/** 内で `_AR_DecodedSample(...)` wrap 経由で decode
  - hit-miss detection: 残存 sample 呼出があれば warning log

### Notes

- 1 build あたり patched .hlsl ~38 file 生成 (= 3-5 MB disk 書込み)、 cache hit で再利用
- Stale file は CacheJanitor で 7 日 sweep
- lilToon source 更新時は手動で Generated/Shaders/_AR_inc_*.hlsl を削除 (= cache 強制 invalidate)
- v0.33+ で lilToon source content hash を cache key に追加予定 (= 自動 invalidate)

## [0.32.3] — 2026-05-05 (Phase 1 alpha hotfix #2)

### Fixed

- **UniversalRewrite mode で encryption は成功するが decode が失敗する致死 bug**: `TransformLtsPassContent` で `ApplyUniversalRewriteIfEnabled` を `InjectHookIntoHlslInclude` の **後** に呼んでいたため、 emit したばかりの OVERRIDE_MAIN macro 内部の `LIL_SAMPLE_2D_LOD(_MainTex, ...)` が rewriter に wrap されて `_AR_DecodedSample(LIL_SAMPLE_2D_LOD(_MainTex, ...), ...)` になっていた。 これで `_AR_DecodedSample` の decode 後に OVERRIDE_MAIN body の自前 XOR decode が **二重適用** され、 結果として **完全 noise** が描画されていた (= 「テクスチャは難読化されたが復元失敗」 の真因)。
- **修正**: rewriter 適用を `InjectHookIntoHlslInclude` の **前** に移動。 元 lts.shader / ltspass.shader 内に LIL_SAMPLE_2D 呼出は 0 件 (Phase 1 では Includes/ recursive walk 未実装) のため、 rewriter は実質 no-op となり、 後で注入される OVERRIDE_* 内部 sample は touched されない。

## [0.32.2] — 2026-05-05 (Phase 1 alpha hotfix)

### Fixed

- **UniversalRewrite mode で encryption が完全に skip されていた致死 bug**: 2 つの根本原因を fix:
  1. **Import 順序 bug**: `LilToonShaderInjector.GenerateLockedVariant` で lts shader を ltspass shader より先に ImportAsset していた (= UsePass 依存解決順序逆)。 Legacy mode は既存 cache file で動作回避していたが universal mode の new hash で fresh compile すると `Shader.Find` が persistent に null 返却 → fallback で original material 維持 → encryption 完全失効。
  2. **Phase 1 で OVERRIDE_* macro skip が早すぎた**: universal mode で OVERRIDE_*-per-spec macro emission を skip していたが、 Phase 1 では lilToon の `Includes/*.hlsl` 内 sample 呼出を rewrite する仕組みが未実装のため、 OVERRIDE_* skip = decode 経路完全消失 → 暗号化 texture が生 noise として render される結果に。 Phase 1 では OVERRIDE_* を **常時 emit** に戻し、 universal helper は **dead code として共存** させる (Phase 2 で Includes/ recursive walk 実装後に skip を有効化予定)。
- **Shader.Find fallback 強化**: ImportAsset 直後の Shader.Find 失敗時に `AssetDatabase.LoadAssetAtPath<Shader>(ltsOutPath)` で直接 load を試みる fallback を追加。

### Phase 1 alpha 完成度

- LegacyOverride mode: v0.31.15 完全互換 (regression なし)
- UniversalRewrite mode: 既存 3 properties (`_MainTex` / `_BumpMap` / `_AlphaMask`) に対する encryption が legacy と同等動作。 universal helper は dead code として shader 内に存在 (= Phase 2 で activate 予定)。

## [0.32.1] — 2026-05-05

### Added

- AntiRippingTagEditor の Texture Pixel Encryption section に `EncryptionMode` dropdown UI 追加。
- Strip Unencrypted Texture Refs / Acknowledge VRCFury Leak の toggle UI 追加。
- UniversalRewrite mode 選択時の HelpBox + 「対象テクスチャ」 個別 toggle の disable scope。

## [0.32.0] — 2026-05-05 (Phase 1 alpha — universal pipeline 基盤)

### Added (Phase 1: pipeline 基盤、 既存 3 properties で動作検証)

- **Universal texture encryption pipeline 基盤**: lilToon の全 .hlsl の `LIL_SAMPLE_2D[*]` 呼出を encrypted property の場合に `_AR_DecodedSample(...)` で wrap する source rewrite engine を新設。
  - `Editor/Pipeline/HlslSourceRewriter.cs`: 5 variants (LIL_SAMPLE_2D / _LOD / _BIAS / _GRAD / _ST) の regex + balanced-paren parse + comma split。 hit-miss detection で encrypted property に対する直接 sample 呼出残存を検出。
  - `Editor/Pipeline/HlslIncludeWalker.cs`: shader source からの include 関係を recursive 解決、 38 file DAG + header guard 認識。
  - `Editor/Pipeline/TexturePropertyDiscovery.cs`: source shader Properties block の全 Texture2D 宣言を regex で parse。
  - `Editor/Pipeline/EncryptionContext.cs`: encryption mode + encrypted property set + cache key + hit-miss errors の context data class。
  - `Editor/Pipeline/CacheJanitor.cs`: `Generated/Shaders/` 配下 7 日経過 stale file の build-start sweep。
- **`EncryptionMode { LegacyOverride, UniversalRewrite }` enum** を `Runtime/AntiRippingTag.cs` に追加。 default = LegacyOverride (Phase 1 では既存動作維持)。
- **`AcknowledgeVRCFuryLeak` opt-in flag**: VRCFury 検出時の build error / warning 抑制 (default OFF、 検出時に user 明示承認を要求する設計)。
- **`LilToonShaderInjector.GenerateLockedVariant(Shader, LockedShaderMode, EncryptionContext)` overload**: universal pipeline 用 API。 既存呼出 `GenerateLockedVariant(Shader, LockedShaderMode)` は backward-compat (= encContext = null で LegacyOverride 互換)。
- **`_AR_DecodedSample` HLSL helper**: fd 不参照 5 引数設計 (sampled / uv / seedLo / seedHi / texelSize / linearize) で全 lilToon variant matrix (NORMAL/LITE/MULTI/FUR/GEM/REF × Forward/Outline/Meta/Shadow) で compile 通る。 v0.31.13 emission 致死 regression を構造的回避。
- **Cache key 拡張**: encryption mode + encrypted property set hash を追加し、 universal mode で property set が違えば別 generated shader を生成 (stale cache 構造的防止)。

### Changed

- `ShaderLockPass.Run` で tag.EncryptionMode に応じて `EncryptionContext` を構築、 `LilToonShaderInjector.GenerateLockedVariant` の universal overload に渡す。
- AntiRippingPlugin.Apply 開始時に `CacheJanitor.SweepStaleFiles` を呼ぶ (= Editor 起動時 reimport 遅延 + line ending warning 緩和)。
- Apply() 開始時の log dump に `EncryptionMode` を追加。

### Phase 1 scope (= alpha 動作検証)

- `EncryptionMode = UniversalRewrite` で既存 3 properties (`_MainTex` / `_BumpMap` / `_AlphaMask`) が universal pipeline 経由でも正常動作する verification。
- HitMissErrors は warning log のみ (Phase 2+ で build error 化予定)。
- Phase 2 で emission / matcap / outline / rim / 2nd / shadow tinting visual-critical properties に拡張予定。
- Phase 3 で全 ~59 properties auto-discovery + `EncryptionMode.UniversalRewrite` を default 化予定。
- Phase 4 (v0.34.0) で legacy OVERRIDE_*-per-property 完全削除予定。

## [0.31.15] — 2026-05-05

### Fixed

- **顔 SMR (multi-material) が真っ白になる visual regression** (v0.31.14 で初めて visible 化): `StripUnencryptedTextureRefs = true` (default ON) が face material の `_EmissionMap` 等を null に剥がし、 lilToon が `_EmissionColor` (default 白) のみで描画 → 顔全体が白光。 `ShaderLockPass.s_StripPreserveList` を新設して emission / matcap / outline / rim / 2nd / shadow tinting / detail / glitter / fur 等の **major rendering texture (~50 properties)** を strip 対象外にし、 visual fidelity を維持。 これらは引き続き material から抽出可能なため leak risk は残存するが、 主要視覚要素 (`_MainTex` / `_BumpMap` / `_AlphaMask`) の暗号化は維持される。
- 経緯: v0.31.12 で導入した strip は元々 visual fidelity 損失を許容する trade-off だったが、 user の v0.31.12 testing は `enableTexturePixelEncryption=false` だったため strip が起動せず、 v0.31.13 は shader compile fail で original lilToon が走り strip 効果が無効化、 v0.31.14 で初めて「encryption 有効 + compile 成功 + strip 起動」が揃って visible 化した。
- 将来 v0.32 で各 property 専用 OVERRIDE_* macro が完成すれば、 preserve list の texture も暗号化されて strip 対象になる予定。

## [0.31.14] — 2026-05-05

### Fixed

- **致死 regression (v0.31.13)**: `_EmissionMap` 暗号化を導入した OVERRIDE_EMISSION_1ST inline が Outline / Meta / Shadow pass や LIL_LITE / LIL_MULTI variant で `fd.invLighting` / `fd.albedo` / `lilCalcBlink` / `lilBlendColor` / `LinearToGammaSpace` 等を未定義参照 → 大量 shader compile error → 全 renderer が BlendShape lock fallback → 元 shader 使用 → **全 texture 露出** していた。
- v0.31.13 で追加した `_EmissionMap` 関連 (spec entry / TextureKind.ColorEmission / BuildValueXorOverrideEmission1st / `encryptEmissionMap` toggle) を全部 revert し、 v0.31.12 動作 (MainTex / NormalMap / AlphaMask 暗号化) に戻した。
- emission 暗号化は将来 `#ifdef LIL_PASS_FORWARD` 等の pass gate と variant gate を考慮した再設計で再導入予定。

## [0.31.0] — 2026-05-04

### Added

- **Texture Pixel Encryption** (実験的): lilToon material の `_MainTex` / `_BumpMap` (NormalMap) / `_AlphaMask` を CPU XOR PRNG (LCG) ストリーム暗号化。 抽出 PNG が完全 noise になり、 復元には injected shader 一式が必要。 (`_Main2ndTex` は次バージョン予定)
- Multi-material renderer 用 asset-only path: visible texture lock は無いが asset 暗号化は universal に効く
- 圧縮形式 enum (`BC7` / `BC3` / `BC1` / `ETC2_RGBA` / `ASTC_4x4` / `Uncompressed`) を Inspector dropdown で選択可能
- XOR Sort + Mapping Mode (推奨): TiledRaster + Morton 3D sort で BC7 ε を構造的に最小化
- 独立 texture decode key `_AR_TK0..3` (vertex decode `_AR_K0..3` と分離して SMR/MR 共通動作)
- `Window/紫陽花広場/Anti-Ripping/Setting` メニューを追加 (window を明示的に開く専用エントリ)
- AntiRippingWindow 上部に banner、 下部に BOOTH / Discord / Ko-fi リンク + 紫陽花広場 Works ブランド + version footer

### Changed

- Step 3 Inspector UI を 3 カテゴリに再編:
  - 🛡 **視覚保護** (オレンジ強調、 暖色 tint card): 見た目に直接影響する Mesh Lock + Shader 復号 + テクスチャ暗号化
  - 📋 **流出追跡** (青): 鍵不要、 アセット透かし + Hierarchy 透かし + lilToon 保護 + Build Fingerprint
  - 🎭 **識別防止難読化** (緑): 動作不変、 GameObject / BlendShape / AnimatorController / アセット名 + Decoy + Shuffle
- テクスチャ暗号化の advanced 項目 (解像度・圧縮形式・モード) を foldout に格納してデフォルト UI を簡潔化
- Banner / Footer 共有 helper class (`AntiRippingBranding`) を Inspector / Window 両方で利用

### Fixed

- multi-material asset-only path で K も bake すると元 mesh の UV6/7 で予期せぬ vertex displacement → mesh 復元失敗 (K=0 維持で解決)
- GPU sampler vs shader cast の精度差で texel 境界 1 texel ずれ → skin texture に horizontal banding (波模様) → UV を texel center に snap (`(floor(uv*size)+0.5)/size`) で解決
- 暗号化 asset 名露出 (`UV1_Base 1_AREnc_MainTex` 等) → `_<16hex>` random 生成で解決
- LilToonShaderInjector cache hash に injection 内容を含めて stale cache 防止

### Compatibility

- Unity 2022.3+
- Required: NDMF 1.4.0+ / Modular Avatar 1.10.0+
- Recommended: lilToon (texture encryption は lilToon 限定)、 Poiyomi (shader-level decode 対応)
