using nadena.dev.ndmf;
using UnityEngine;

namespace AjisaiFlow.AntiRipping
{
    /// <summary>
    /// v0.31: encrypted texture の圧縮形式選択肢。
    /// 既存 bool toggle (compressEncryptedTextureBC7) との互換性を保つため、
    /// AutoFromToggle (= 0) を default として持ち、 これは旧 bool 値に従う動作。
    /// それ以外は明示的に format を指定する。
    /// 各形式の特性:
    ///  - Uncompressed (RGBA32): lossless、 4 byte/pixel、 BC7 ε ノイズ完全回避だがサイズ 4×
    ///  - BC7: PC 向け高品質、 1 byte/pixel、 alpha 連動 mode 5/7 で precision 落ちる懸念あり
    ///  - BC3 (DXT5): alpha 対応、 1 byte/pixel、 5-6-5 RGB precision (BC7 より低い)
    ///  - BC1 (DXT1): alpha 無し (alpha → 1.0 強制)、 0.5 byte/pixel、 5-6-5 precision、 サイズ最小
    ///  - ETC2_RGBA: モバイル向け、 1 byte/pixel、 PC GPU では software decode の可能性
    ///  - ASTC_4x4: 高品質可変、 1 byte/pixel、 モバイル・モダン PC 対応
    /// </summary>
    public enum EncryptedTextureCompression
    {
        /// <summary>旧 compressEncryptedTextureBC7 toggle に従う (legacy migration)</summary>
        AutoFromToggle = 0,
        /// <summary>RGBA32 lossless (BC7 ε ノイズ完全回避、 サイズ 4×)</summary>
        Uncompressed = 1,
        /// <summary>BC7: PC 向け高品質</summary>
        BC7 = 2,
        /// <summary>BC3 / DXT5: alpha 対応の古典 PC 形式</summary>
        BC3 = 3,
        /// <summary>BC1 / DXT1: alpha 無し、 サイズ最小</summary>
        BC1 = 4,
        /// <summary>ETC2 RGBA: モバイル向け</summary>
        ETC2_RGBA = 5,
        /// <summary>ASTC 4×4 RGBA: 高品質可変</summary>
        ASTC_4x4 = 6,
    }

    /// <summary>
    /// アバタールートに 1 つだけ貼って使う Editor 専用コンポーネント。
    /// ビルド時に NDMF パスがこのコンポーネントを検出し、設定された保護レイヤーをアバターに焼き込む。
    /// INDMFEditorOnly を実装しているため、ビルド成果物には残らない。
    ///
    /// v0.3: Expression PIN を削除。OSC 経由のキー配送に一本化。
    /// 鍵はユーザーが Inspector の「鍵を作成」ボタンを押した時点で生成・永続化される。
    /// 同じ鍵が複数ビルドにまたがって使われるため、再ビルドで OSC のやり直しは不要。
    /// </summary>
    [AddComponentMenu("紫陽花広場/VRChat Anti-Ripping (NDMF Script)")]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-9000)]
    public sealed class AntiRippingTag : MonoBehaviour, INDMFEditorOnly
    {
        // ────────────────────────────── 作者情報 ──────────────────────────────

        [Tooltip("ウォーターマークに埋め込む作者名 / ハンドル名 (必須)")]
        [SerializeField] private string creatorName = "";

        [Tooltip("ライセンス URL または BOOTH 商品ページなど (任意だが推奨)")]
        [SerializeField] private string licenseUrl = "";

        [Tooltip("作者連絡先 (任意): X URL, Discord ID, mail 等")]
        [SerializeField] private string contactInfo = "";

        // ────────────────────────────── 追跡保護 (v0.1) ──────────────────────────────

        [Tooltip("全 material に作者ハッシュ float (_AjisaiAR_Hash) を焼き込む。\n" +
                 "shader 動作は変わらず、 AssetBundle 内の .mat に build id 由来の hash が残るため、\n" +
                 "流出 .mat → ProtectionReport (Logs~) の build id 突合で身元証明に使える。")]
        [SerializeField] private bool enableAssetWatermark = true;

        [Tooltip("アバター階層に作者情報を含む不可視 GameObject を散りばめる")]
        [SerializeField] private bool enableHierarchyWatermark = true;

        [Tooltip("ビルドごとに固有 ID を生成し、流出時の追跡に使えるレポートを Assets/紫陽花広場/anti-ripping/Logs~/ に書き出す")]
        [SerializeField] private bool enableBuildFingerprint = true;

        // ────────────────────────────── 表示阻止 (v0.2) ──────────────────────────────
        // v0.3 で撤廃した enableMeshLock トグルを v0.34.20 で再導入。
        // スコープは MeshLockPass の BlendShape 頂点 scramble のみ。
        // shader-level decode (enableShaderLevelDecode) / texture 暗号化 (enableTexturePixelEncryption) /
        // KeyAnimator / OSC sender / Unlock manifest は独立に動作する。
        [Tooltip("MeshLockPass の BlendShape 頂点散乱 (mesh を locked variant に差し替えて頂点位置を擬似ランダムに変位) を有効化する。\n" +
                 "OFF: 頂点散乱のみ skip。 shader-level decode / texture 暗号化 / KeyAnimator / OSC sender は\n" +
                 "それぞれの toggle (enableShaderLevelDecode / enableTexturePixelEncryption) で個別に制御される。\n" +
                 "頂点散乱を切ると AABB が膨らまない一方、 BlendShape 経路の解錠耐性は失われる。")]
        [SerializeField] private bool enableMeshLock = true;

        [Tooltip("collapse-to-point の収束点まわりの微小ジッタ距離 (メートル)。\n" +
                 "ロック中は描画停止 (m_Enabled=0) されるため bounds は元メッシュ境界のままで AABB は肥大しない。\n" +
                 "force-enable 時は頂点が一点に収束 (collapse-to-point) し面積ゼロの縮退三角形になる (overdraw ほぼゼロ)。\n" +
                 "この値は収束点まわりの微小ジッタ (保険) のみに影響する。")]
        [Range(0.05f, 2.0f)]
        [SerializeField] private float meshLockScrambleRadius = 0.05f;

        [Tooltip("ON: VRChat の保存パラメータに OSC で 1 回書けば次回以降自動復元 (推奨)\n" +
                 "OFF: 毎セッション AntiRippingClient による OSC 送信が必要 (より安全)")]
        [SerializeField] private bool meshLockKeySaved = true;

        [Tooltip("解錠キー (16 文字 hex = 64 bit / 8 byte)。Inspector の「鍵を作成」ボタンで生成。\n" +
                 "鍵が空の場合はビルド時に Mesh Lock がスキップされる (警告ログ)。\n" +
                 "v0.9 から 32bit → 64bit に拡張 (純粋総当たり耐性 ~1.8×10^19 通り)。")]
        [SerializeField] private string meshLockKeyHex = "";

        [Tooltip("解錠 OSC パラメータ名 8 つ (v0.9 で 4 → 8 個)。\n" +
                 "鍵生成時に毎回ランダムな 16 文字 hex で命名されるため、\n" +
                 "アドレス自体が秘密の一部となる (アドレス + 値の二重防御)。")]
        [SerializeField] private string[] meshLockParamNames = new string[0];

        [Tooltip("即時ロック用の bool パラメータ名。\n" +
                 "Expression Menu の「🔒 Lock Avatar」と AntiRippingClient の「ロック」ボタンで\n" +
                 "値 1 にされ、Animator Layer 2 (ForceLock) が BlendShape weight=0 を override 駆動する。")]
        [SerializeField] private string meshLockNowParamName = "";

        [Tooltip("解錠状態の broadcast 用 bool パラメータ名 (synced)。\n" +
                 "ローカルで K 値が一致したことを示す 1 bit を全クライアントへ同期するため、\n" +
                 "リモート視聴者にも復元アバターが見えるようになる。\n" +
                 "鍵自体は同期しないので、この bool が抜かれても鍵は漏れない。")]
        [SerializeField] private string meshLockBroadcastParamName = "";

        [Tooltip("Unlock BlendShape 名 (鍵生成時にランダム化)。\n" +
                 "従来の固定名 _AjisaiAR_Unlock では AssetRipper でメッシュを開いた瞬間に\n" +
                 "「これを 100 にすれば復元される」と分かってしまうので、" +
                 "16 文字 hex でランダム化して攻撃者の試行コストを上げる。")]
        [SerializeField] private string meshLockUnlockBlendShapeName = "";

        [Tooltip("MA インストール用ホルダー GameObject 名 (鍵生成時にランダム化)。\n" +
                 "従来の固定名 _AjisaiAR_Lock は ARC 系の anti-anti-ripping 攻撃で\n" +
                 "「Lock」prefix 一致を狙い撃ちされる弱点があるため、 16 文字 hex でランダム化して\n" +
                 "Hierarchy で他プラグインが生成する _<16hex> 名前と視覚的に揃える。")]
        [SerializeField] private string meshLockHolderObjectName = "";

        [Tooltip("AAP score 用 float パラメータ名 (localOnly)。\n" +
                 "Animator の Direct Blend Tree が K0..K7 から score を累積計算する。\n" +
                 "解錠判定はこの float vs 定数の Greater/Less で行うため、平文 K 値が transition に現れない。")]
        [SerializeField] private string meshLockScoreParamName = "";

        [Tooltip("AAP 用「常時 1.0」float パラメータ名 (localOnly, default=1)。\n" +
                 "Direct Blend Tree の Direct Blend Param として 8 個の child を全て active にする。")]
        [SerializeField] private string meshLockOneParamName = "";

        [Tooltip("Shader-level decode の補助 AAP パラメータ名 (localOnly Float, default=1.0)。\n" +
                 "1 - broadcast を計算する内部用 parameter で ShaderLockPass の controller のみ参照する。\n" +
                 "鍵生成時にランダム化することで AssetRipper で AnimatorController を解析されても\n" +
                 "ShaderLockPass の存在 / 役割を識別困難にする (従来の固定名 _AjisaiAR_InvBroadcast 撤廃)。")]
        [SerializeField] private string meshLockInvBroadcastParamName = "";

        [Tooltip("各 K_i に乗じる salt 値 (8 byte / 1〜255)。\n" +
                 "score = Σ (K_i × salt_i) / 255 で計算され、鍵が一致したときだけ expected と等しくなる。\n" +
                 "salt はクリップに埋もれるため、attacker は 1 つの expected 値から 8 個の鍵を逆算する必要がある。")]
        [SerializeField] private byte[] meshLockSalts = new byte[0];

        [Tooltip("v0.13+: シェーダーレベル復号 (default: ON)。\n" +
                 "lilToon / Poiyomi のソース shader をコピー + textual injection で locked variant を生成し、\n" +
                 "頂点 shader 内で UV6/UV7 と _AR_K0..3 から直接復号する。\n" +
                 "見た目は元 shader (lilToon / Poiyomi) と完全に同じまま、\n" +
                 "AnimatorController を解析されても鍵は露出せず、メッシュに復元情報が残らない。\n" +
                 "対応 shader が無い material や multi-material renderer は BlendShape lock に自動フォールバック。\n" +
                 "OFF にすると BlendShape lock のみで保護される (shader 解析耐性は弱まる)。")]
        [SerializeField] private bool enableShaderLevelDecode = true;

        [Tooltip("v0.37+: MeshRenderer を SkinnedMeshRenderer に build 時に型変換し、 BlendShape 経路で\n" +
                 "mesh-level scramble する。 MR の Safety Mode 対応 (= custom shader 無効化時も形状復元) が完全化される。\n" +
                 "対象: lockable shader (lilToon / Poiyomi 系) を持つ MeshRenderer のみ。 非対応 shader の MR は変換されない。\n" +
                 "変換ロジック: Mesh を clone + 全頂点 weight=1.0 で root bone bind + bindposes=identity +\n" +
                 "SMR component 追加 (Renderer base prop 完全コピー) + 元 MR/MeshFilter Destroy。\n" +
                 "副作用: VRChat Performance Stat の SMR 数が増え、 Rank downgrade のリスクあり (Quest では特に影響大)。\n" +
                 "build 時に Rank 計算し downgrade した場合は ARLog.Warn で警告ログ出力。\n" +
                 "default OFF (= opt-in)。 Quest 向け build にも適用される (ユーザー責任で判断)。")]
        [SerializeField] private bool enableMeshRendererToSkinnedConversion = false;

        [Tooltip("Shader-level Decode で追加でロック対象に含めたい shader 名 (部分一致、大文字小文字無視)。\n" +
                 "例: 'XSToon'、'Sunao'。\n" +
                 "lilToon / Poiyomi 派生は自動検出されるのでここに書く必要は無い。\n" +
                 "(現状の injector は lilToon と Poiyomi のみ対応。それ以外の shader は\n" +
                 " 注入失敗 → BlendShape lock にフォールバック。)")]
        [SerializeField] private string[] extraShaderNamesToLock = new string[0];

        [Tooltip("v0.33.9+: Shader-Lock の対象から **除外** する shader 名 (部分一致、 大文字小文字無視)。\n" +
                 "lilToon カスタムシェーダー (BoundBonePro lilToonSquish 等、 .lilcontainer / .lilblock 経由で\n" +
                 "生成された派生 shader) で ビルド後 material が pink (shader compile error) になる場合に、\n" +
                 "shader 名の一部 (例: 'BoundBonePro'、 'lilToonSquish') を追加すると当該 material の shader-lock\n" +
                 "を skip できる。 skip された material は BlendShape lock + texture 非暗号化で出力される。\n" +
                 "(自動検出: lilToon 公式 package 配下でない 'lilToon' を含む shader は自動的に skip される。\n" +
                 " このリストは自動検出で拾えない shader への手動 override。)")]
        [SerializeField] private string[] excludeFromShaderLock = new string[0];

        // v0.37.8: テクスチャ暗号化からマテリアル単位で除外する list。
        // 除外された material は shader-lock は通常通り通る (= mesh-level 保護維持) が、 texture pixel encryption は
        // skip される (= 元 texture が AssetBundle に焼かれて leak 可能、 visual は安定)。
        // 用途: MatCap 等の微調整 texture で暗号化精度損失が visual に影響する material を leak 許容で除外したいケース。
        // 元 (src) material reference で比較する (= avatar prefab に貼られている original material)。
        [Tooltip("v0.37.8+: テクスチャ暗号化から除外する material (= leak 許容)。\n" +
                 "指定した material は shader-lock は通常通り通る (= mesh-level 保護維持) が、 texture pixel\n" +
                 "encryption が skip され、 元テクスチャが AssetBundle にそのまま焼かれる (= AssetRipper で抽出可能)。\n" +
                 "用途: MatCap 等の微調整テクスチャで暗号化精度損失による visual 違和感を leak 許容で回避したいケース。\n" +
                 "重要: 列挙した material の **全テクスチャ** が leak 対象になる。 main color texture も含まれる場合は\n" +
                 "保護効果が大幅に下がるため慎重に検討してください。")]
        [SerializeField] private Material[] excludeFromTextureEncryption = new Material[0];

        // ────────────────────────────── Texture Pixel Encryption (v0.31, 実験的) ──────────────────────────────

        [Tooltip("v0.31+ (実験的、 default OFF): lilToon material の主要 texture を CPU 側で XOR PRNG (LCG)\n" +
                 "ストリーム暗号化し、 AssetBundle に焼き込まれる texture asset 自体を完全 noise 化する。\n" +
                 "shader 内で K0..K3 の鍵が一致したときのみ runtime 復号して描画 (lilToon の OVERRIDE_* macro hook 経由)。\n" +
                 "AssetRipper で抽出した PNG は noise として保存され、 元画像復元には injected shader 一式\n" +
                 "(= avatar build 成果物) が必要になる。\n" +
                 "制約 (v0.31.x): lilToon のみ。 multi-material renderer は asset-only path で全 slot 暗号化\n" +
                 "(visible texture lock は無し、 mesh は BlendShape lock で scramble)、\n" +
                 "Safety mode (Custom Shader OFF) では noise 表示のまま (= 仕様)。\n" +
                 "副作用: 圧縮 texture (BC7/DXT5) が RGBA32 に変換 → AssetBundle サイズが約 4 倍に膨張する。\n" +
                 "v0.30 UV scramble の撤回経緯と v0.23 致死バグ (SetTexture が locked variant shader を破壊) の\n" +
                 "リスクをふまえ、 default OFF (opt-in)。 安定確認後 v0.32 で default ON 化を検討。")]
        [SerializeField] private bool enableTexturePixelEncryption = false;

        [Tooltip("master が ON のとき有効: _MainTex (diffuse base) を暗号化する。\n" +
                 "RGB チャネルに XOR (alpha は cutout edge 保護のため触らない)。")]
        [SerializeField] private bool encryptMainTex = true;

        [Tooltip("master が ON のとき有効: _NormalMap (法線) を暗号化する。\n" +
                 "Unity の DXT5nm packing (BA に法線 X/Y) を尊重し、 RG channel のみに XOR。\n" +
                 "v0.31.0 (MVP) では未実装。 v0.31.x で対応予定。")]
        [SerializeField] private bool encryptNormalMap = true;

        [Tooltip("master が ON のとき有効: _Main2ndTex (secondary color layer) を暗号化する。\n" +
                 "_MainTex と同じ RGB XOR。\n" +
                 "v0.31.0 (MVP) では未実装。 v0.31.x で対応予定。")]
        [SerializeField] private bool encryptMain2nd = true;

        [Tooltip("master が ON のとき有効: _AlphaMask (transparency) を暗号化する。\n" +
                 "R channel のみに XOR (G/B/A は意味を持たない)。\n" +
                 "v0.31.0 (MVP) では未実装。 v0.31.x で対応予定。")]
        [SerializeField] private bool encryptAlphaMask = true;

        // v0.31.14 revert: encryptEmissionMap toggle は v0.31.13 で導入したが、
        // OVERRIDE_EMISSION_1ST inline 展開が複数 lilToon variant で compile error
        // (`fd.invLighting` / `fd.albedo` / `lilCalcBlink` 等が未定義) → 全 renderer 元 shader fallback
        // → 全 texture 露出という致死 regression を起こしたため、 spec entry / 専用 builder と一緒に撤去。
        // serialized field 自体も削除 (= v0.31.13 で保存された値は次の Save Project で消える、 機能無効のため無害)。

        [Tooltip("暗号化 texture の最大解像度 (px、 縦横の長辺)。 これを超える元 texture は GPU Blit で\n" +
                 "downsample してから暗号化する。 AssetBundle サイズ膨張対策。\n" +
                 "・XOR PRNG decode は非可逆圧縮 (BC7/DXT5) と原理的に両立できないため encrypted texture は\n" +
                 "  必ず RGBA32 (= 4 byte/pixel) で保存する必要がある。\n" +
                 "・default 2048: 1K/2K texture はそのまま、 4K texture のみ 2K に downsample (4× サイズ削減)。\n" +
                 "・1024 にすると 2K も downsample されてアグレッシブに削減できるが、 顔/肌の精細度が落ちる。\n" +
                 "・8192 にすると downsample 無効化 (全 texture 元解像度のまま)。\n" +
                 "サイズ目安: 4K RGBA32 = 64 MB、 2K RGBA32 = 16 MB、 1K RGBA32 = 4 MB")]
        [Range(256, 8192)]
        [SerializeField] private int textureEncryptionMaxResolution = 2048;

        [Tooltip("v0.31 (legacy): 暗号化 texture を BC7 圧縮する旧 bool toggle。\n" +
                 "v0.31.x で encryptedTextureCompression enum (下) に置き換え予定。\n" +
                 "enum が AutoFromToggle (default) のときのみこの bool が参照される。\n" +
                 "enum を Uncompressed / BC7 / BC3 / BC1 / ETC2 / ASTC のいずれかに設定すると enum 側が優先される。")]
        [SerializeField] private bool compressEncryptedTextureBC7 = false;

        [Tooltip("v0.31.x: 暗号化 texture の圧縮形式選択。\n" +
                 "・AutoFromToggle (default): 旧 BC7 bool toggle に従う (= 互換)\n" +
                 "・Uncompressed (RGBA32): lossless、 BC7 ε ノイズ完全回避、 サイズ 4× (推奨: 品質優先時)\n" +
                 "・BC7: PC 向け高品質 1 byte/pixel、 alpha 変動で mode 5/7 選択され precision 低下の懸念\n" +
                 "・BC3 (DXT5): alpha 対応、 5-6-5 RGB precision (BC7 より低)\n" +
                 "・BC1 (DXT1): alpha 無し (alpha=1.0 強制で透明度消失)、 サイズ最小 0.5 byte/pixel\n" +
                 "・ETC2_RGBA: モバイル向け、 PC GPU では software decode 可能性\n" +
                 "・ASTC_4x4: モバイル・モダン PC 両対応、 高品質可変 1 byte/pixel\n" +
                 "形式によっては Unity 側で対応 GPU 不在時に CPU decode フォールバックや RGBA32 自動展開される。\n" +
                 "ノイズ・サイズ・品質のトレードオフを user が直接試行可能。")]
        [SerializeField] private EncryptedTextureCompression encryptedTextureCompression = EncryptedTextureCompression.AutoFromToggle;

        [Tooltip("v0.31 実験的★推奨: XOR Sort + Mapping mode (default OFF)。\n" +
                 "1. 全 pixel に Value XOR (= 完全 random byte に)\n" +
                 "2. **Global sort**: 全 pixel を luminance 順に並び替え、 texture 上から下に向かって配置\n" +
                 "   (= 隣接 BC7 block 同士の color が連続的に変化、 boundary noise なし)\n" +
                 "3. sort permutation を別 mapping texture (= 元解像度の RGBA32) に保存\n" +
                 "4. shader は per-fragment で mapping から (sx, sy) 取得 → encrypted を sample → XOR decode\n" +
                 "結果: BC7 圧縮 + 完全 noise (safety mode 耐性) + 復元時クリーン (ノイズなし)\n" +
                 "サイズ: encrypted (BC7) + mapping (RGBA32 同解像度) = 5 byte/pixel (RGBA32 単独の 1.25 倍)\n" +
                 "BC7 OFF にすると encrypted も RGBA32 = 8 byte/pixel (デバッグ用、 lossless decode)。")]
        [SerializeField] private bool useTextureXorSortMappingMode = false;

        // ── v0.34.7: Fallback shader 用 placeholder (= VRChat Safety で shader fallback 中の他 user に低解像度 preview を見せる) ──
        [Tooltip("v0.34.7: VRChat Safety で shader fallback 中の他ユーザーに低解像度の placeholder texture を表示する。\n" +
                 "OFF の場合、 fallback shader 利用者は暗号化済 noise を albedo として描画してしまう (旧挙動)。\n" +
                 "ON の場合、 _MainTex slot に低解像度 placeholder を bind し、 暗号化済 RGBA32 は別 property\n" +
                 "(_AR_Enc_MainTex) に格納される。 locked variant shader は _AR_Enc_MainTex を読むため見た目変化なし。\n" +
                 "default ON 推奨 (体験改善 vs わずかな VRAM 増のトレードオフ)。")]
        [SerializeField] private bool showFallbackPlaceholder = true;

        [Tooltip("v0.34.7: Fallback placeholder texture の解像度 (px)。\n" +
                 "・16: モザイク状、 シルエット判別困難 (protection 最大)\n" +
                 "・64 (default): ぼやけシルエット視認可、 protection と体験の妥協点\n" +
                 "・256: ほぼ判別可能、 protection 効果が薄れる\n" +
                 "VRAM 影響: 64x64 RGBA32 で +16 KB / texture (無視できる)。")]
        [Range(16, 256)]
        [SerializeField] private int fallbackPlaceholderResolution = 64;

        // ── v0.34.0+: Universal LIL_SAMPLE_* wrapper category groups ──

        [Tooltip("v0.34.11 Stage 1 で activate: Mask group (single-channel mask ~17 prop) を一括暗号化対象にする。\n" +
                 "対象: _MainColorAdjustMask, _Main{2,3}rdBlendMask, _Main{2,3}rdDissolveMask, _MatCapBlendMask,\n" +
                 "      _OutlineWidthMask, _ShadowBorderMask, _ShadowBlurMask, _RimShadeMask, _DissolveMask,\n" +
                 "      _FurNoiseMask, _FurMask, _FurLengthMask, _AnisotropyScaleMask 等。\n" +
                 "skip (Stage 1): _MetallicGlossMap, _SmoothnessTex, _GlitterShapeTex, _TriMask (channel pack 系) /\n" +
                 "                _ParallaxMap (height map 誤分類)。\n" +
                 "default ON (Stage 1 安全範囲、 既存 v0.34.10 user は serialized field 値を維持)。")]
        [SerializeField] private bool encryptMaskGroup = true;

        [Tooltip("v0.34.11 Stage 1 で activate: Color group (sRGB color ~9 prop) を一括暗号化対象にする。\n" +
                 "対象: _MainGradationTex, _OutlineTex, _Shadow{1,2,3}ColorTex, _BacklightColorTex, _ReflectionColorTex,\n" +
                 "      _MatCap{1,2}Tex, _RimColorTex, _GlitterColorTex。\n" +
                 "skip (Stage 1): _Main2ndTex / _Main3rdTex (= face decal、 lilGetSubTex 経由で v0.34.2 alpha 全失敗、\n" +
                 "               Stage 3 R&D 待ち)。\n" +
                 "default ON (Stage 1 安全範囲、 既存 v0.34.10 user は serialized field 値を維持)。")]
        [SerializeField] private bool encryptColorGroup = true;

        [Tooltip("v0.34.11 Stage 1 で activate: Normal group (法線 ~6 prop) を一括暗号化対象にする。\n" +
                 "対象: _Bump2ndMap, _MatCapBumpMap, _MatCap2ndBumpMap, _AnisotropyTangentMap, _OutlineVectorTex,\n" +
                 "      _FurVectorTex。\n" +
                 "default ON (Stage 1 安全範囲、 既存 v0.34.10 user は serialized field 値を維持)。")]
        [SerializeField] private bool encryptNormalGroup = true;

        [Tooltip("v0.34.4+ で activate: Emission group (発光 5 prop) を一括暗号化対象にする。\n" +
                 "対象: _EmissionMap, _Emission2ndMap, _EmissionGradTex, _Emission2ndGradTex 等。\n" +
                 "v0.31.13 で OVERRIDE_EMISSION_1ST inline 展開で致死 regression を起こした教訓を踏まえ、\n" +
                 "v0.34.4 では universal wrapper 方式 (= sample primitive 1 点のみ介入) で実装。\n" +
                 "lilToon body の context (fd.invLighting / fd.albedo / lilCalcBlink 等) には一切 touch しないため\n" +
                 "全 lilToon variant (LIL_LITE / MULTI / REFRACTION × Forward / Outline / Meta / Shadow) で互換性 100% 構造保証。\n" +
                 "default OFF (opt-in、 段階 release で最後に活性化)。")]
        [SerializeField] private bool encryptEmissionGroup = false;

        // ── v0.34.15 (案 Y): lilToon カスタム派生 shader 互換 mode ──

        [Tooltip("v0.34.15 で activate: lilToon カスタム派生 shader (= shader name に 'lilToon' を含むが asset path が\n" +
                 "jp.lilxyzw.liltoon 配下でないもの) を使う material を shader-lock / texture encryption の対象から\n" +
                 "完全 skip する。\n" +
                 "対象例:\n" +
                 "  - BoundBonePro lilToonSquish ('Hidden/BoundBonePro/lilToonSquish/*')\n" +
                 "  - lilToon Inspector の「カスタムシェーダー作成」 で生成された .lilcontainer / .lilblock 派生\n" +
                 "  - サードパーティ vendor の lilToon 派生 (= custom.hlsl include / lilCustomVertexWS hook 拡張等)\n" +
                 "これらは公式 lilToon と異なる include 構造 / vertex displacement / 独自 hook を持ち、\n" +
                 "VRCAAR の lilToon wrapper 注入では shader 生成失敗 → BlendShape lock fallback + nullify safety mode に\n" +
                 "なり material が真っ白に表示されてしまう (v0.34.13/14 の null 化制御アプローチでは完全復元不可)。\n" +
                 "本 toggle ON 時、 該当 material は元のまま維持 (= visual 完全復元、 派生機能無傷)。\n" +
                 "Trade-off: 該当 material の暗号化対象 prop は完全 leak 許容 (= AssetRipper 抽出可能)。\n" +
                 "公式 lilToon material および Poiyomi material は通常通り暗号化されるため protection は維持。\n" +
                 "default ON (カスタム派生 shader 利用 avatar での visual 破綻を default で防ぐ)。 false にすると従来挙動\n" +
                 "(= 該当 material が BlendShape lock fallback + nullify safety mode、 v0.34.12 以前と同等で真っ白になる)。")]
        [SerializeField] private bool skipCustomLilToonDerivatives = true;

        // ── Emergency disable switches (build-time、 group-level rollback) ──
        [Tooltip("v0.34.0+: 緊急時に Mask group を build-time で完全 disable (= group toggle ON でも暗号化 skip)。\n" +
                 "user が「v0.34.1 以降を入れたら〇〇が崩れた」と報告した際の 1-click partial rollback 用。 default OFF。")]
        [SerializeField] private bool disableMaskGroup = false;

        [Tooltip("v0.34.0+: 緊急時に Color group を build-time で完全 disable。 default OFF。")]
        [SerializeField] private bool disableColorGroup = false;

        [Tooltip("v0.34.0+: 緊急時に Normal group を build-time で完全 disable。 default OFF。")]
        [SerializeField] private bool disableNormalGroup = false;

        [Tooltip("v0.34.0+: 緊急時に Emission group を build-time で完全 disable。 default OFF。")]
        [SerializeField] private bool disableEmissionGroup = false;

        [Tooltip("v0.31.12+: texture pixel encryption ON 時、 暗号化対象外の texture 参照 (= _MainTex / _BumpMap / _AlphaMask 以外) を locked variant material から null に剥がす。\n" +
                 "v0.31.15 改良: visual-critical な property (emission / matcap / outline / rim / 2nd / shadow tinting / detail / glitter / fur 等の major rendering texture) は preserve list で保持される。\n" +
                 "結果として剥がされるのは: detail mask / id mask / NDMF 一時 等の **minor rendering 用 texture** のみ → 顔白化 / 服色 collapse 等の visual fidelity 損失なし。\n" +
                 "preserve list 詳細は ShaderLockPass.s_StripPreserveList を参照。\n" +
                 "効果: build 出力の material asset から minor rendering texture への参照は除去 (= 一部 leak 防止)。 emission / matcap / outline 等の major texture は引き続き material から抽出可能 (= leak risk 残存)。\n" +
                 "default ON (= 軽量 strip)。 OFF にすると全 texture 参照が material に残る。\n" +
                 "v0.32 で各 property 専用 OVERRIDE_* macro が完成すれば、 preserve list の texture も暗号化されて strip 対象になる予定。")]
        [SerializeField] private bool stripUnencryptedTextureRefs = true;

        [Tooltip("v0.32: VRCFury が avatar 内に存在する場合、 VRCFury が runtime 動的生成する material/shader は\n" +
                 "AntiRipping の build-time encryption pipeline を構造的に bypass する。 該当 material は元 texture が leak する。\n" +
                 "この flag を ON にすると VRCFury 検出時の build error / warning を抑制し、 leak risk を user が明示承認した扱いになる。\n" +
                 "default OFF: VRCFury 検出時に build を停止し、 user に leak risk を明示通知する。")]
        [SerializeField] private bool acknowledgeVRCFuryLeak = false;

        [Tooltip("v0.17+: ビルド時に Renderer GameObject 名をランダム文字列 (_16hex) に置換する。\n" +
                 "AssetRipper で抽出された avatar の Unity Project で「どれが顔/髪/服か」を識別困難にする。\n" +
                 "Humanoid bone は対象外 (Avatar binding 破壊回避)。\n" +
                 "AnimationClip の path binding は NDMF AnimatorServicesContext で自動 remap される。\n" +
                 "副作用: Hierarchy / Inspector で当該 GO を後追いするのが debug 困難になる。")]
        [SerializeField] private bool enableGameObjectObfuscation = false;

        [Tooltip("v0.19+: ビルド時に各 SkinnedMeshRenderer の sharedMesh の **BlendShape 名そのもの** を\n" +
                 "_<16hex> にランダム rename する。 順序 (index) は維持するため index ベース binding は無傷。\n" +
                 "AnimationClip / VRC Viseme / MA ShapeChanger / BlendshapeSync の name 形式参照は全て同期 rewrite。\n" +
                 "AssetRipper 抽出時に「Smile_L」「vrc.v_aa」等の意味のある名前が消える (Decoy より強力)。\n" +
                 "GameObject 難読化と同じく NDMF AnimatorServicesContext 経由で全 plugin の clip も rewrite される。")]
        [SerializeField] private bool enableBlendShapeObfuscation = false;

        [Tooltip("v0.37+: BlendShape 難読化 ON 時に MMD ワールド用の標準モーフ (= あ / い / う / え / お / ω / にこり / まばたき / ハイライト消し 等) を\n" +
                 "rename 対象から除外する (= 順序シャッフルには参加するが名前は元のまま維持される)。\n" +
                 "MMD DanceController は BlendShape 名で SetBlendShapeWeight を呼ぶため、 rename されると MMD ワールドで表情が動かなくなる。\n" +
                 "また 「-------MMD-------」「=======MMD=======」 等の section divider (= 名前に「MMD」 を含む BS) も自動的に除外される。\n" +
                 "default ON (= MMD 互換性を確保)。 OFF にすると従来挙動 (= 全 BS rename、 MMD 表情が壊れる) になる。")]
        [SerializeField] private bool excludeMmdBlendShapes = true;

        [Tooltip("v0.21+: ビルド時に AnimatorController の Layer 名 / State 名 / StateMachine 名 / Parameter 名を\n" +
                 "_<16hex> にランダム rename する。 Transition condition / VRCAvatarParameterDriver / VRCExpressionParameters /\n" +
                 "VRCExpressionsMenu / MA ModularAvatarParameters の参照は全て同期 rewrite される。\n" +
                 "VRC client 必須 parameter (Viseme / Voice / Gesture / IsLocal 等) と anti-ripping 自身の鍵関連 parameter\n" +
                 "(K0..K7 / LockNow / Broadcast / Score / One) は rename 対象から除外され機能に影響しない。\n" +
                 "Optimizing phase で実行されるため MA / FaceEmo / lilycal-Inventory 等の controller も全て覆われる。")]
        [SerializeField] private bool enableAnimatorObfuscation = false;

        [Tooltip("v0.37+: Animator パラメータ難読化 ON 時に VRCOSC (心拍計 / SpeechToText 等の外部 OSC アプリ) が\n" +
                 "書き込むパラメータ (= 名前が「VRCOSC/」 で始まるもの、 例: VRCOSC/Heartrate/Average 等) を\n" +
                 "rename 対象から除外する。\n" +
                 "VRCOSC アプリは外部から OSC で /avatar/parameters/VRCOSC/Heartrate/Average 等を送信するため、\n" +
                 "rename されると avatar 側で受信できず心拍計などが機能停止する。\n" +
                 "default ON (= VRCOSC 互換性を確保)。 OFF にすると従来挙動 (= 全 param rename、 VRCOSC が壊れる)。")]
        [SerializeField] private bool excludeVrcOscParameters = true;

        [Tooltip("v0.36+: ON で Animator パラメータ難読化を決定論的 (再現可能) にする。\n" +
                 "同じ元パラメータ名は常に同じ難読名になり、PC と Quest を別々にビルドしても一致する。\n" +
                 "enableAnimatorObfuscation が ON のときのみ効果あり。 既定 OFF (ビルド毎ランダム)。")]
        [SerializeField] private bool deterministicObfuscation = false;

        [Tooltip("決定論的難読化の安定シード (32 文字 hex)。 トグル ON 時に Inspector が自動生成。\n" +
                 "PC/Quest を別 prefab で作る場合は両 prefab に同じ値を設定する。")]
        [SerializeField] private string obfuscationSeedHex = "";

        [Tooltip("v0.23+: avatar が参照する Mesh / Material / AnimationClip / AvatarMask の\n" +
                 "**アセット名そのもの** を _<16hex> にランダム rename する。\n" +
                 "v0.25+: AnimatorController アセット名も対象。\n" +
                 "v0.28+: VRCExpressionsMenu (再帰) / VRCExpressionParameters のアセット名も対象。\n" +
                 "AssetRipper 抽出時に「Mesh_Body」「Smile_Anim」「MainMenu」等の意味のある名前が消え、\n" +
                 "GameObject / BlendShape / Animator 難読化と組み合わせて抽出物全体が _<16hex> で埋まり識別困難に。\n" +
                 "元 prefab アセットは Object.Instantiate で deep clone されてから rename されるため非破壊。\n" +
                 "Shader は Shader.Find('lilToon') 等の name 依存があるため対象外。\n" +
                 "Texture は NDMF 一時 (AAO Atlas 等) のみ rename、 元 prefab は rename しない\n" +
                 "(= shader-level decode を壊す致死バグ防止)。")]
        [SerializeField] private bool enableAssetNameObfuscation = false;

        [Tooltip("v0.18+: ビルド時に各 SkinnedMeshRenderer の sharedMesh 末尾に dummy BlendShape を\n" +
                 "ランダム数 (32〜64 個) 追加し、 AssetRipper で抽出された mesh で本物の Viseme/表情/装飾の\n" +
                 "BlendShape が「意味不明な _<16hex> dummy 群」に紛れて識別困難になる。\n" +
                 "本物の BlendShape は名前・順序・index を完全保存するため、 Animator / MA / FaceEmo / AAO\n" +
                 "等の参照は string・index 両形式とも全く無傷 (= ギミックは絶対に壊れない)。\n" +
                 "v0.26+: dummy の delta は「元 mesh の既存 BlendShape をランダムに 1 つ選び、\n" +
                 "ランダム係数 ∈ [-1.5,-0.3]∪[0.3,1.5] でスケール」したものを使用するため、\n" +
                 "本物と同じ性質の delta になり、 「全頂点 0」フィルタでの bulk 識別が不能になる。\n" +
                 "v0.27.4+: dummy の SMR weight 分布は「20% で {0, 100, [0,100] 連続乱数} 3 択 + 80% で [0,100] 整数乱数」。\n" +
                 "結果として weight=0 ~7.5%、 weight=100 ~7.5%、 その他 (整数+小数) ~85% で、\n" +
                 "Inspector の BlendShape リストの「末尾 _<16hex> が全部 0」 block が消え、\n" +
                 "「on/off 設定」+「微調整」+「ランダム整数」が混じる本物っぽい分布になる。\n" +
                 "weight 非 0 dummy は delta=0 で形状不変を保証 (~7.5% の weight=0 dummy のみ varied delta)。\n" +
                 "副作用: mesh ファイルサイズは source BlendShape のスパース性に依存 (顔表情なら 1 dummy あたり数 KB〜数十 KB)。")]
        [SerializeField] private bool enableBlendShapeDecoy = false;

        [Tooltip("Decoy として追加する dummy BlendShape の最低個数。 ビルドごとにこの値〜MaxCount の間でランダム決定される。")]
        [Range(8, 128)]
        [SerializeField] private int blendShapeDecoyMinCount = 32;

        [Tooltip("Decoy として追加する dummy BlendShape の最大個数。 ビルドごとに MinCount〜この値の間でランダム決定される。")]
        [Range(8, 128)]
        [SerializeField] private int blendShapeDecoyMaxCount = 64;

        // ────────────────────────────── Decoy Animator (v0.28) ──────────────────────────────

        [Tooltip("v0.28+: 攻撃者撹乱用に「ダミー SMR + ダミー BlendShape + ダミー Material + ダミー AnimationClip\n" +
                 " + ダミー AnimatorController (依存チェーン Layer 構成)」を avatar root 直下に注入する。\n" +
                 "新規 Decoy controller は MA MergeAnimator で本物 FX に統合され、 Optimizing phase で\n" +
                 "Layer / State / Parameter / Asset 名がすべて _<16hex> 化されるため、 本物の解錠 chain\n" +
                 "(K0..K7 → Score → Broadcast → Display) とダミーが視覚的に区別不能になる。\n" +
                 "ダミー parameter は全て localOnly=true なので VRChat synced parameter budget (256bit) を消費しない。\n" +
                 "ダミー BlendShape は Animator から実際に駆動 (constant 0 curve) されるため AAO TraceAndOptimize で削除されない。\n" +
                 "副作用: Hierarchy / Animator / Asset 一覧に大量の _<16hex> が並ぶため debug 性能が下がる。")]
        [SerializeField] private bool enableDecoyAnimator = false;

        [Tooltip("ダミー SMR の最低個数。 ビルドごとに この値〜MaxCount の間でランダム決定される。")]
        [Range(0, 16)]
        [SerializeField] private int decoyRendererMinCount = 2;

        [Tooltip("ダミー SMR の最大個数。 ビルドごとに MinCount〜この値の間でランダム決定される。")]
        [Range(0, 16)]
        [SerializeField] private int decoyRendererMaxCount = 5;

        [Tooltip("各ダミー SMR に追加するダミー BlendShape の個数。 全ダミーで一律。")]
        [Range(1, 64)]
        [SerializeField] private int decoyBlendShapePerRenderer = 8;

        [Tooltip("ダミー parameter 依存チェーンの最低段数。 例えば 2 なら param A → Layer1 → param B → Layer2 → BlendShape。")]
        [Range(1, 6)]
        [SerializeField] private int decoyParameterChainMin = 2;

        [Tooltip("ダミー parameter 依存チェーンの最大段数。 ビルドごとに Min〜この値の間でランダム決定される。")]
        [Range(1, 6)]
        [SerializeField] private int decoyParameterChainMax = 3;

        [Tooltip("各 chain 段で生成するダミー parameter の個数 (= 同段の dummy layer 数)。")]
        [Range(1, 16)]
        [SerializeField] private int decoyParametersPerChain = 4;

        // ────────────────────────────── Hierarchy Shuffle (v0.29) ──────────────────────────────

        [Tooltip("v0.29+: avatar 配下の各 Transform の子順序 (sibling index) を Fisher-Yates でランダム並び替えする。\n" +
                 "AssetRipper 抽出時に「上から Body / Hair / Outfit / 装飾」のような直感的順序が消え、\n" +
                 "攻撃者が hierarchy 構造から avatar の組成を推測する手間が増える。\n" +
                 "sibling 順序は AnimationClip path / Mecanim Humanoid / SMR.bones[] のいずれにも影響しないため\n" +
                 "副作用ゼロ (Animator / 物理 / 表情 / メニューすべて完全に無傷)。\n" +
                 "副作用: Inspector / Hierarchy で順序が毎ビルドごとに変わるため debug 性能↓。")]
        [SerializeField] private bool enableHierarchyShuffle = false;

        // ────────────────────────────── 詳細 ──────────────────────────────

        [Tooltip("ビルド時に Console へ詳細ログを出すか")]
        [SerializeField] private bool verboseLogging = false;

        // v0.37.8 導入 / v0.38: default ON。 生成 shader を build 間で保持して Unity ShaderCache を有効化。
        // OFF だと build 終了時に Generated/Shaders/_AR_*.shader が全削除され、 次 build で Unity は
        // 新 shader 扱いで全 variant を 1 から compile し直す (= 多 material avatar で数十分)。
        // ON で sweep を skip し、 content-skip (WriteIfChanged) と組み合わせて同一内容なら write/import
        // を省略 → cache hit で compile を数分単位に短縮する。 旧ファイルは GeneratedShaderSweepPass が
        // version sentinel ベースで掃除する (同 version は全保持)。
        // trade-off: locked shader file が disk に残るため attacker が shader 構造を解析可能になる
        // (ただし texture 復号には _AR_TK0..3 = Animator AAP score が必要で、 shader 抽出だけでは
        // texture 復号は不可)。 解析耐性を最大化したい配布時は OFF にできる (任意)。
        [Tooltip("生成シェーダーをビルド間で保持してビルド時間を短縮 (default ON)\n" +
                 "効果: Unity ShaderCache が hit し shader compile が大幅短縮 (= 多 material avatar で数十分→数分)。\n" +
                 "trade-off: locked shader file が Generated/Shaders/_AR_*.shader として disk に残り、\n" +
                 "解析者が shader 構造を読める。 ただし texture 復号には Animator AAP score 累積が必要なため、\n" +
                 "shader 単体抽出では texture は復号できない (= protection が完全に失われるわけではない)。\n" +
                 "default ON。 解析耐性を最大化したい配布時のみ OFF にできる (任意)。")]
        [SerializeField] private bool keepGeneratedShadersBetweenBuilds = true;

        // ────────────────────────────── プロパティ ──────────────────────────────

        public string CreatorName => creatorName;
        public string LicenseUrl => licenseUrl;
        public string ContactInfo => contactInfo;

        public bool EnableAssetWatermark => enableAssetWatermark;
        public bool EnableHierarchyWatermark => enableHierarchyWatermark;
        public bool EnableBuildFingerprint => enableBuildFingerprint;

        public bool EnableMeshLock => enableMeshLock;
        public float MeshLockScrambleRadius => meshLockScrambleRadius;
        public bool MeshLockKeySaved => meshLockKeySaved;
        public string MeshLockKeyHex => meshLockKeyHex;
        // v0.9: 16 文字 hex (8 byte / 64 bit)。旧形式 (8 文字 hex / 32 bit) は無効扱い → 再生成必要
        public bool HasMeshLockKey => !string.IsNullOrEmpty(meshLockKeyHex) && meshLockKeyHex.Length == 16;
        public const int KeyByteCount = 8;

        public string[] MeshLockParamNames => meshLockParamNames ?? new string[0];
        public bool HasMeshLockParamNames => meshLockParamNames != null && meshLockParamNames.Length == KeyByteCount
            && System.Array.TrueForAll(meshLockParamNames, n => !string.IsNullOrEmpty(n));

        public string MeshLockNowParamName => meshLockNowParamName ?? "";
        public bool HasMeshLockNowParamName => !string.IsNullOrEmpty(meshLockNowParamName);

        public string MeshLockBroadcastParamName => meshLockBroadcastParamName ?? "";
        public bool HasMeshLockBroadcastParamName => !string.IsNullOrEmpty(meshLockBroadcastParamName);

        public string MeshLockUnlockBlendShapeName => meshLockUnlockBlendShapeName ?? "";
        public bool HasMeshLockUnlockBlendShapeName => !string.IsNullOrEmpty(meshLockUnlockBlendShapeName);

        public string MeshLockHolderObjectName => meshLockHolderObjectName ?? "";
        public bool HasMeshLockHolderObjectName => !string.IsNullOrEmpty(meshLockHolderObjectName);

        public bool EnableShaderLevelDecode => enableShaderLevelDecode;

        // v0.37+: MR→SMR 変換 toggle (= lockable shader を持つ MR を build 時に SMR 化)。
        // EnableShaderLevelDecode との AND を取る (= shader-level decode OFF 時は変換しても意味がない)。
        public bool EnableMeshRendererToSkinnedConversion =>
            enableMeshRendererToSkinnedConversion && enableShaderLevelDecode;

        public string[] ExtraShaderNamesToLock => extraShaderNamesToLock ?? new string[0];
        public string[] ExcludeFromShaderLock => excludeFromShaderLock ?? new string[0];

        // ── Texture Pixel Encryption (v0.31) ──
        // master が OFF のとき子 toggle は全て無効化される。
        // shader-level decode が OFF のときも texture encryption は意味を持たない (= shader hook が動かない)
        // ため、 EnableTexturePixelEncryption は EnableShaderLevelDecode との AND を取る。
        public bool EnableTexturePixelEncryption => enableTexturePixelEncryption && enableShaderLevelDecode;
        /// <summary>diagnostic: master toggle 値そのもの (= EnableTexturePixelEncryption が false のとき shaderLevelDecode との切り分けに使う)</summary>
        public bool _DiagEnableTexturePixelEncryptionMaster => enableTexturePixelEncryption;
        public bool EncryptMainTex => EnableTexturePixelEncryption && encryptMainTex;
        public bool EncryptNormalMap => EnableTexturePixelEncryption && encryptNormalMap;
        public bool EncryptMain2nd => EnableTexturePixelEncryption && encryptMain2nd;
        public bool EncryptAlphaMask => EnableTexturePixelEncryption && encryptAlphaMask;

        // ── v0.34.0+: Universal wrapper category group accessors ──
        // 各 group toggle は v0.34.x 段階 release で対応 prop の IsEnabled lambda に評価される。
        // disable switch が ON だと group toggle 値に関わらず disable 扱い (緊急 rollback)。
        public bool EncryptMaskGroup     => EnableTexturePixelEncryption && encryptMaskGroup     && !disableMaskGroup;
        public bool EncryptColorGroup    => EnableTexturePixelEncryption && encryptColorGroup    && !disableColorGroup;
        public bool EncryptNormalGroup   => EnableTexturePixelEncryption && encryptNormalGroup   && !disableNormalGroup;
        public bool EncryptEmissionGroup => EnableTexturePixelEncryption && encryptEmissionGroup && !disableEmissionGroup;

        // v0.34.15: lilToon カスタム派生 shader 互換 mode accessor (BBP / 「カスタムシェーダー作成」 派生 / サードパーティ vendor 派生 等)
        public bool SkipCustomLilToonDerivatives => skipCustomLilToonDerivatives;

        // v0.31.14 revert: EncryptEmissionMap property は v0.31.13 で追加したが致死 regression のため撤去。
        // v0.31.12: locked variant material から暗号化対象外 texture 参照を剥がす (= leak 防止、 visual fidelity 損失あり)
        // EnableTexturePixelEncryption が ON でない時は意味がない (= 暗号化 texture 参照自体が無い) ので AND ゲート。
        public bool StripUnencryptedTextureRefs => EnableTexturePixelEncryption && stripUnencryptedTextureRefs;
        public bool AcknowledgeVRCFuryLeak => acknowledgeVRCFuryLeak;
        public int TextureEncryptionMaxResolution => Mathf.Clamp(textureEncryptionMaxResolution, 256, 8192);
        public bool CompressEncryptedTextureBC7 => compressEncryptedTextureBC7;

        /// <summary>
        /// v0.31.x: encrypted texture の圧縮形式を解決する。 AutoFromToggle のときは旧 bool に従う。
        /// 戻り値:
        ///  - null: 圧縮しない (RGBA32 そのまま、 lossless)
        ///  - それ以外: 該当 TextureFormat (BC7 / DXT5 / DXT1 / ETC2_RGBA8 / ASTC_RGBA_4x4)
        /// 呼び出し側 (TextureFormatHelper.CreateEncryptedTexture) は null なら圧縮 skip、
        /// それ以外なら EditorUtility.CompressTexture(target, format, Best) を呼ぶ。
        ///
        /// v0.37.8: XorSortMapping mode OFF のときは <strong>強制 Uncompressed</strong>。
        /// 理由: ValueXor mode + 任意圧縮形式 (BC7/BC3/BC1/ETC2/ASTC) は XOR amplification で
        /// モザイクノイズ発生する致命的組合せ。 XorSortMapping mode は sort + 6-bit rounded 設計で
        /// BC7 ε ≈ 0 を構造保証するため、 圧縮との両立が成立する。
        /// enum 値は保存したまま (= XorSortMapping mode を後で ON 復帰したら元の選択値が自動復活)。
        /// </summary>
        public TextureFormat? GetEncryptedTextureCompressionFormat()
        {
            // v0.37.8 構造的 safety gate: XorSortMapping mode OFF なら無条件 Uncompressed。
            if (!useTextureXorSortMappingMode)
            {
                return null;
            }

            var mode = encryptedTextureCompression;
            // AutoFromToggle: 旧 bool に従う (BC7 ON → BC7、 OFF → 圧縮なし)
            if (mode == EncryptedTextureCompression.AutoFromToggle)
            {
                mode = compressEncryptedTextureBC7
                    ? EncryptedTextureCompression.BC7
                    : EncryptedTextureCompression.Uncompressed;
            }
            switch (mode)
            {
                case EncryptedTextureCompression.BC7: return TextureFormat.BC7;
                case EncryptedTextureCompression.BC3: return TextureFormat.DXT5;
                case EncryptedTextureCompression.BC1: return TextureFormat.DXT1;
                case EncryptedTextureCompression.ETC2_RGBA: return TextureFormat.ETC2_RGBA8;
                case EncryptedTextureCompression.ASTC_4x4: return TextureFormat.ASTC_4x4;
                case EncryptedTextureCompression.Uncompressed:
                default:
                    return null;
            }
        }

        /// <summary>選択中の圧縮形式 (AutoFromToggle 解決後の最終 mode)。 ログ・Inspector 表示用。</summary>
        public EncryptedTextureCompression ResolvedEncryptedTextureCompression
        {
            get
            {
                // v0.37.8: XorSortMapping mode OFF なら強制 Uncompressed (= GetEncryptedTextureCompressionFormat と同じ logic)
                if (!useTextureXorSortMappingMode)
                {
                    return EncryptedTextureCompression.Uncompressed;
                }

                var mode = encryptedTextureCompression;
                if (mode == EncryptedTextureCompression.AutoFromToggle)
                {
                    mode = compressEncryptedTextureBC7
                        ? EncryptedTextureCompression.BC7
                        : EncryptedTextureCompression.Uncompressed;
                }
                return mode;
            }
        }
        public bool UseTextureXorSortMappingMode => useTextureXorSortMappingMode;

        // v0.34.7: Fallback placeholder
        public bool ShowFallbackPlaceholder => showFallbackPlaceholder;
        public int FallbackPlaceholderResolution => Mathf.Clamp(fallbackPlaceholderResolution, 16, 256);
        public bool EnableGameObjectObfuscation => enableGameObjectObfuscation;
        public bool EnableBlendShapeObfuscation => enableBlendShapeObfuscation;

        // v0.37+: MMD 標準モーフを BlendShape 難読化対象から除外 (default ON、 MMD 互換性確保)
        public bool ExcludeMmdBlendShapes => excludeMmdBlendShapes;
        public bool EnableAnimatorObfuscation => enableAnimatorObfuscation;

        // v0.37+: VRCOSC 等の外部 OSC アプリの parameter を Animator 難読化対象から除外 (default ON)
        public bool ExcludeVrcOscParameters => excludeVrcOscParameters;

        public bool DeterministicObfuscation => deterministicObfuscation;
        public string ObfuscationSeedHex => obfuscationSeedHex ?? "";
        public bool EnableAssetNameObfuscation => enableAssetNameObfuscation;
        public bool EnableBlendShapeDecoy => enableBlendShapeDecoy;
        public int BlendShapeDecoyMinCount => Mathf.Clamp(blendShapeDecoyMinCount, 0, 128);
        public int BlendShapeDecoyMaxCount => Mathf.Clamp(blendShapeDecoyMaxCount, BlendShapeDecoyMinCount, 128);

        public bool EnableDecoyAnimator => enableDecoyAnimator;
        public int DecoyRendererMinCount => Mathf.Clamp(decoyRendererMinCount, 0, 16);
        public int DecoyRendererMaxCount => Mathf.Clamp(decoyRendererMaxCount, DecoyRendererMinCount, 16);
        public int DecoyBlendShapePerRenderer => Mathf.Clamp(decoyBlendShapePerRenderer, 1, 64);
        public int DecoyParameterChainMin => Mathf.Clamp(decoyParameterChainMin, 1, 6);
        public int DecoyParameterChainMax => Mathf.Clamp(decoyParameterChainMax, DecoyParameterChainMin, 6);
        public int DecoyParametersPerChain => Mathf.Clamp(decoyParametersPerChain, 1, 16);

        public bool EnableHierarchyShuffle => enableHierarchyShuffle;

        /// <summary>
        /// 与えられた shader が AntiRipping の lock 対象とみなせるかを判定する。
        /// 自動対象: lilToon 派生 / Poiyomi 派生 (".poiyomi/..." または "Poiyomi" を含む)。
        /// 加えて <c>ExtraShaderNamesToLock</c> に指定された shader 名 (部分一致、大文字小文字無視) も対象。
        /// v0.33.9+: <c>ExcludeFromShaderLock</c> に指定された shader 名 (部分一致) は強制的に対象外にする
        /// (lilToon カスタムシェーダーで pink バグが起きるケースの override)。
        /// </summary>
        public bool ShouldLockShader(Shader shader)
        {
            if (shader == null || string.IsNullOrEmpty(shader.name)) return false;

            bool match = false;
            // lilToon 派生は無条件で対象
            if (shader.name.IndexOf("lilToon", System.StringComparison.OrdinalIgnoreCase) >= 0) match = true;
            // Poiyomi 派生も自動対象 (".poiyomi/Poiyomi Toon" 等、Thry-locked 変種 "Hidden/Locked/.poiyomi/..." も拾う)
            else if (shader.name.IndexOf(".poiyomi/", System.StringComparison.OrdinalIgnoreCase) >= 0) match = true;
            else if (shader.name.IndexOf("Poiyomi", System.StringComparison.OrdinalIgnoreCase) >= 0) match = true;
            else
            {
                // ユーザー指定の追加対象
                foreach (var s in ExtraShaderNamesToLock)
                {
                    if (string.IsNullOrEmpty(s)) continue;
                    if (shader.name.IndexOf(s, System.StringComparison.OrdinalIgnoreCase) >= 0) { match = true; break; }
                }
            }
            if (!match) return false;

            // v0.33.9+: 手動 exclude override (= match を false に倒す)。
            // lilToon カスタムシェーダー (BoundBonePro lilToonSquish 等) で pink バグが起きる場合の救済策。
            // shader-lock を skip → BlendShape lock + texture 非暗号化にフォールバック。
            foreach (var s in ExcludeFromShaderLock)
            {
                if (string.IsNullOrEmpty(s)) continue;
                if (shader.name.IndexOf(s, System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }
            return true;
        }

        public string MeshLockScoreParamName => meshLockScoreParamName ?? "";
        public string MeshLockOneParamName => meshLockOneParamName ?? "";
        public byte[] MeshLockSalts => meshLockSalts ?? new byte[0];

        /// <summary>
        /// AAP 累積方式の鍵情報がそろっているか (v0.10 以降の鍵かどうか)。
        /// </summary>
        public bool HasMeshLockAAP =>
            !string.IsNullOrEmpty(meshLockScoreParamName) &&
            !string.IsNullOrEmpty(meshLockOneParamName) &&
            meshLockSalts != null && meshLockSalts.Length == KeyByteCount;

        /// <summary>
        /// 旧データ互換: LockNow 名が無ければ固定名にフォールバック。
        /// </summary>
        public string GetLockNowParamName() => HasMeshLockNowParamName ? meshLockNowParamName : "_AjisaiAR_LockNow";

        public string GetBroadcastParamName() => HasMeshLockBroadcastParamName ? meshLockBroadcastParamName : "_AjisaiAR_Unlocked";

        public string GetUnlockBlendShapeName() => HasMeshLockUnlockBlendShapeName ? meshLockUnlockBlendShapeName : "_AjisaiAR_Unlock";

        public string GetHolderObjectName() => HasMeshLockHolderObjectName ? meshLockHolderObjectName : "_AjisaiAR_Lock";

        public string GetScoreParamName() => string.IsNullOrEmpty(meshLockScoreParamName) ? "_AjisaiAR_Score" : meshLockScoreParamName;
        public string GetOneParamName() => string.IsNullOrEmpty(meshLockOneParamName) ? "_AjisaiAR_One" : meshLockOneParamName;
        public string GetInvBroadcastParamName() =>
            string.IsNullOrEmpty(meshLockInvBroadcastParamName) ? "_AjisaiAR_InvBroadcast" : meshLockInvBroadcastParamName;

        /// <summary>
        /// パラメータ名を取り出す。未生成の場合は固定名にフォールバック (旧データ動作維持用)。
        /// </summary>
        public string GetParamName(int index)
        {
            if (index < 0 || index >= KeyByteCount) return null;
            if (HasMeshLockParamNames) return meshLockParamNames[index];
            return $"_AjisaiAR_K{index}";
        }

        public bool VerboseLogging => verboseLogging;

        public bool KeepGeneratedShadersBetweenBuilds => keepGeneratedShadersBetweenBuilds;

        public Material[] ExcludeFromTextureEncryption => excludeFromTextureEncryption ?? new Material[0];

        /// <summary>
        /// v0.37.8: src material が ExcludeFromTextureEncryption list に含まれているか判定。
        /// 含まれていれば texture pixel encryption を skip する (= shader-lock は通常通り通る、 mesh-level 保護維持)。
        /// reference 比較で、 prefab に貼られている original material のみマッチする (= clone 後の locked variant は別 reference)。
        /// </summary>
        public bool IsTextureEncryptionExcluded(Material srcMat)
        {
            if (srcMat == null || excludeFromTextureEncryption == null || excludeFromTextureEncryption.Length == 0)
                return false;
            for (int i = 0; i < excludeFromTextureEncryption.Length; i++)
            {
                if (excludeFromTextureEncryption[i] == srcMat) return true;
            }
            return false;
        }

        public bool HasMinimalConfig() => !string.IsNullOrWhiteSpace(creatorName);

        /// <summary>
        /// 16 文字 hex を 8 byte 配列に変換。失敗時は all-zero。
        /// </summary>
        public byte[] GetMeshLockKeyBytes()
        {
            if (!HasMeshLockKey) return new byte[KeyByteCount];
            try
            {
                var bytes = new byte[KeyByteCount];
                for (int i = 0; i < KeyByteCount; i++)
                {
                    bytes[i] = byte.Parse(
                        meshLockKeyHex.Substring(i * 2, 2),
                        System.Globalization.NumberStyles.HexNumber);
                }
                return bytes;
            }
            catch
            {
                return new byte[KeyByteCount];
            }
        }
    }
}
