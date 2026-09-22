using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace AjisaiFlow.AntiRipping
{
    /// <summary>画像ごとの暗号化の設定。設計書 §3.3。</summary>
    [System.Serializable]
    public sealed class TextureOverride
    {
        /// <summary>対象画像の識別子。Editor 側で &lt;guid&gt;:&lt;localId&gt; の形に設定する。</summary>
        public string guid;

        /// <summary>用途 (shader の prop 名)。空なら、その画像の全ての用途に適用する。</summary>
        public string propertyName;

        /// <summary>この画像の解像度の上限。0 なら全体の設定に従う。</summary>
        public int maxResolution;

        /// <summary>0 = 既定、1 = 対象にする、2 = 外す。</summary>
        public int mode;
    }

    /// <summary>
    /// アバタールートに 1 つだけ貼って使う Editor 専用コンポーネント。
    /// ビルド時に NDMF パスがこのコンポーネントを検出し、設定された保護レイヤーをアバターに焼き込む。
    /// INDMFEditorOnly を実装しているため、ビルド成果物には残らない。
    ///
    /// v0.3: Expression PIN を削除。OSC 経由のキー配送に一本化。
    /// 鍵はユーザーが Inspector の「鍵を今すぐ作成」ボタンを押した時点で生成・永続化される。
    /// 同じ鍵が複数ビルドにまたがって使われるため、再ビルドで OSC のやり直しは不要。
    /// </summary>
    [AddComponentMenu("紫陽花広場/VRChat Anti-Ripping (NDMF Script)")]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-9000)]
    public sealed class AntiRippingTag : MonoBehaviour, INDMFEditorOnly
    {
        /// <summary>テクスチャ暗号化で選択できる解像度の段階。</summary>
        public static readonly int[] TextureResolutionSteps =
            { 64, 128, 256, 512, 1024, 2048, 4096, 8192 };

        /// <summary>Fallback placeholder で選択できる解像度の段階。</summary>
        public static readonly int[] PlaceholderResolutionSteps =
            { 16, 32, 64, 128, 256 };

        /// <summary>任意の解像度を最も近いテクスチャ解像度の段階へ丸める。</summary>
        public static int SnapTextureResolution(int value)
        {
            int nearest = TextureResolutionSteps[0];
            long nearestDistance = long.MaxValue;
            for (int i = 0; i < TextureResolutionSteps.Length; i++)
            {
                long distance = value - (long)TextureResolutionSteps[i];
                if (distance < 0L) distance = -distance;
                if (distance < nearestDistance)
                {
                    nearest = TextureResolutionSteps[i];
                    nearestDistance = distance;
                }
            }
            return nearest;
        }

        /// <summary>任意の placeholder 解像度を最も近い段階へ丸める。</summary>
        public static int SnapPlaceholderResolution(int value)
        {
            int clamped = Mathf.Clamp(value, PlaceholderResolutionSteps[0],
                PlaceholderResolutionSteps[PlaceholderResolutionSteps.Length - 1]);
            int nearest = PlaceholderResolutionSteps[0];
            long nearestDistance = long.MaxValue;
            for (int i = 0; i < PlaceholderResolutionSteps.Length; i++)
            {
                long distance = clamped - (long)PlaceholderResolutionSteps[i];
                if (distance < 0L) distance = -distance;
                if (distance < nearestDistance)
                {
                    nearest = PlaceholderResolutionSteps[i];
                    nearestDistance = distance;
                }
            }
            return nearest;
        }

        // ────────────────────────────── 作者情報 ──────────────────────────────

        [Tooltip("ウォーターマークに埋め込む作者名 / ハンドル名 (必須)")]
        [SerializeField] private string creatorName = "";

        [Tooltip("ライセンス URL または BOOTH 商品ページなど (任意だが推奨)")]
        [SerializeField] private string licenseUrl = "";

        [Tooltip("作者連絡先 (任意): X URL, Discord ID, mail 等")]
        [SerializeField] private string contactInfo = "";

        // ────────────────────────────── 追跡保護 (v0.1) ──────────────────────────────

        [Tooltip("全マテリアルにこのビルド固有の作者ハッシュを埋め込みます。見た目や動作は変わりません。\n" +
                 "流出したマテリアルをレポート (Logs~ フォルダ) の記録と照合し、自分のビルドから出たものだと証明できます。")]
        [SerializeField] private bool enableAssetWatermark = true;

        [Tooltip("アバター階層に作者情報を含む不可視 GameObject を散りばめる")]
        [SerializeField] private bool enableHierarchyWatermark = true;

        [Tooltip("ビルドごとに固有 ID を生成し、流出時の追跡に使えるレポートを Assets/紫陽花広場/anti-ripping/Logs~/ に書き出す")]
        [SerializeField] private bool enableBuildFingerprint = true;

        [Tooltip("ビルド後に、難読化の結果・失敗・スキップした項目をまとめたレポートウィンドウを自動で開きます。")]
        [SerializeField] private bool showBuildReportInNdmf = true;

        // ────────────────────────────── 表示阻止 (v0.2) ──────────────────────────────
        // v0.3 で撤廃した enableMeshLock トグルを v0.34.20 で再導入。
        // スコープは MeshLockPass の BlendShape 頂点 scramble のみ。
        // shader-level decode (enableShaderLevelDecode) / texture 暗号化 (enableTexturePixelEncryption) /
        // KeyAnimator / OSC sender / Unlock manifest は独立に動作する。
        [Tooltip("鍵が無いときにメッシュの頂点を散らして形を崩し、正しい鍵で元に戻します。\n" +
                 "OFF にすると頂点散らしだけを行いません (シェーダー復号・テクスチャ暗号化・解錠処理は各トグルで個別に制御)。\n" +
                 "頂点を散らさない分アバターの大きさ判定は膨らみませんが、この経路での保護は弱くなります。")]
        [SerializeField] private bool enableMeshLock = true;

        [Tooltip("ON: VRChat の保存パラメータに OSC で 1 回書けば次回以降自動復元 (推奨)\n" +
                 "OFF: 毎セッション AntiRippingClient による OSC 送信が必要 (より安全)")]
        [SerializeField] private bool meshLockKeySaved = true;

        [Tooltip("ON (既定): SPS / DPS / TPS など、シェーダーで頂点を変形する plug メッシュを検出し、メッシュロックの頂点散らしから自動で除外します。\n" +
                 "これらは頂点を置き換えると変形が壊れるため、既定で除外して互換性を保ちます。個別調整は対象 GameObject の Anti-Ripping Scope Override でも可能です。\n" +
                 "注意: 除外された plug はメッシュロックで保護されません (SPS とは原理的に両立できません)。")]
        [SerializeField] private bool autoExcludeSpsDpsFromMeshLock = true;

        [Tooltip("ON (既定): 検出した SPS / DPS / TPS plug をシェーダーロックとテクスチャ暗号化からも自動で除外します。\n" +
                 "VRCFury はビルド時に plug のシェーダーを書き換えるため、保護シェーダーが対象になると TPS はビルド失敗、SPS は表示不具合のリスクがあります。\n" +
                 "plug が本体とマテリアルを共有している場合は除外しません (本体テクスチャの平文化を防止)。\n" +
                 "注意: 除外された plug のマテリアル・テクスチャは保護されません。")]
        [SerializeField] private bool autoExcludePlugFromShaderLock = true;

        [Tooltip("解錠キー (16 文字の hex)。Inspector の「鍵を今すぐ作成」ボタンで生成します (未生成でもビルド時に自動生成されます)。\n" +
                 "鍵が空の場合、ビルド時にメッシュロックはスキップされます (警告ログ)。")]
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

        [Tooltip("v0.42+: 解錠中に owner が数秒おきに toggle する synced bool パラメータ名 (heartbeat)。\n" +
                 "値は表示に使わず、 toggle のたび VRChat の同期ブロック (解錠 broadcast を含む) を再送させ、\n" +
                 "後から見始めた / アバターを再描画したリモートにも解錠状態が届きやすくする保険。 鍵生成時にランダム化。")]
        [SerializeField] private string meshLockHeartbeatParamName = "";

        [Tooltip("v0.43+: テクスチャ復号鍵 TK0..3 を synced で remote へ届ける OSC/animator パラメータ名 4 つ。\n" +
                 "鍵生成時にランダム 16 文字 hex で命名され、 解錠鍵下位 4 byte (keyBytes[0..3]) を運ぶ。\n" +
                 "synced Int で 0..255 を正確に配送し、 1D-BT-copy で material._AR_TK を復元する。\n" +
                 "旧データ (未生成) は固定名 _AjisaiAR_TK0..3 にフォールバック。")]
        [SerializeField] private string[] meshLockTextureKeyParamNames = new string[0];

        [Tooltip("各 K_i に乗じる salt 値 (8 byte / 1〜255)。\n" +
                 "score = Σ (K_i × salt_i) / 255 で計算され、鍵が一致したときだけ expected と等しくなる。\n" +
                 "salt はクリップに埋もれるため、attacker は 1 つの expected 値から 8 個の鍵を逆算する必要がある。")]
        [SerializeField] private byte[] meshLockSalts = new byte[0];

        [Tooltip("lilToon / Poiyomi のマテリアルを、見た目はそのままに、解錠処理をシェーダー内部で行う保護版に差し替えます (既定 ON)。\n" +
                 "鍵がアニメーターに現れないため解析されにくくなります。lilToon での役割はテクスチャの復号です (メッシュの散らし・復元はメッシュロックが担当します)。\n" +
                 "対応していないシェーダーのマテリアルはそのまま維持されます。\n" +
                 "OFF にするとメッシュ側の保護だけになり、テクスチャ暗号化も無効になります。")]
        [SerializeField] private bool enableShaderLevelDecode = true;

        [Tooltip("MeshRenderer をビルド時に SkinnedMeshRenderer へ変換し、メッシュロックで保護できるようにします。\n" +
                 "VRChat のセーフティ (カスタムシェーダー無効化) 時も形状が正しく復元されます。対象は lilToon / Poiyomi 系シェーダーを持つ MeshRenderer のみです。\n" +
                 "副作用: SkinnedMeshRenderer が増え、Performance Rank が下がる場合があります (特に Quest)。既定 OFF。")]
        [SerializeField] private bool enableMeshRendererToSkinnedConversion = false;

        [Tooltip("自動検出される lilToon / Poiyomi 以外に、保護対象へ含めたいシェーダー名を追加します (一部一致、大文字小文字は無視)。例: 'XSToon'、'Sunao'。\n" +
                 "現在対応しているのは lilToon と Poiyomi のみで、それ以外を指定した場合はメッシュ側の保護に切り替わります。")]
        [SerializeField] private string[] extraShaderNamesToLock = new string[0];

        [Tooltip("シェーダー保護の対象から除外するシェーダー名を指定します (一部一致、大文字小文字は無視)。\n" +
                 "lilToon ベースの特殊シェーダーを使ったマテリアルがビルド後にピンク色になる場合、その名前の一部 (例: 'BoundBonePro') を追加すると除外できます。\n" +
                 "除外したマテリアルはメッシュ側の保護のみ適用され、テクスチャは暗号化されません。")]
        [SerializeField] private string[] excludeFromShaderLock = new string[0];

        // v0.37.8: テクスチャ暗号化からマテリアル単位で除外する list。
        // 除外された material は shader-lock は通常通り通る (= mesh-level 保護維持) が、 texture pixel encryption は
        // skip される (= 元 texture が AssetBundle に焼かれて leak 可能、 visual は安定)。
        // 用途: MatCap 等の微調整 texture で暗号化精度損失が visual に影響する material を leak 許容で除外したいケース。
        // 元 (src) material reference で比較する (= avatar prefab に貼られている original material)。
        [Tooltip("テクスチャ暗号化から除外するマテリアルを指定します (そのテクスチャは抜き取られる可能性を許容)。\n" +
                 "メッシュの保護は通常どおり効きますが、テクスチャは暗号化されず元の画像がそのまま書き出されます。\n" +
                 "用途: MatCap など、暗号化による見た目の変化が気になるマテリアル。\n" +
                 "重要: 指定したマテリアルの全テクスチャが対象です。メインの色テクスチャを含む場合は保護効果が大きく下がるため慎重に選んでください。")]
        [SerializeField] private Material[] excludeFromTextureEncryption = new Material[0];

        // v0.51: テクスチャ (Texture2D asset) 単位でテクスチャ暗号化から除外する list。
        // material 単位除外 (excludeFromTextureEncryption) が material 内の **全テクスチャ** を外すのに対し、 こちらは
        // material 内の **特定 texture asset 1 枚だけ** を暗号化から外せる (= その texture が使われている他 prop / 他
        // material でも一律 skip される。 元 (src) texture asset の reference で比較)。
        // 除外された texture は平文で AssetBundle に焼かれて leak 許容になるが、 shader-lock / mesh 保護は不変。
        // include-only モードとの関係も material 除外と同じで、 除外は常に優先する (include 指定 material 内でも除外
        // 指定した texture は暗号化しない)。
        // 用途: MatCap・グラデ等、 暗号化の精度損失が visual に出る texture を「1 枚だけ」leak 許容で外したいケース。
        [Tooltip("テクスチャ暗号化から外す個別のテクスチャを 1 枚単位で指定します (そのテクスチャは抜き取られる可能性を許容)。\n" +
                 "マテリアル単位の除外と違い、同じマテリアル内でも指定した 1 枚だけを平文のまま書き出せます。メッシュ・シェーダーの保護は変わりません。\n" +
                 "用途: MatCap・グラデーションなど、暗号化による見た目の変化が気になるテクスチャ。\n" +
                 "ホワイトリスト指定より常に優先されます。")]
        [SerializeField] private Texture2D[] excludeTexturesFromEncryption = new Texture2D[0];

        // v0.42: テクスチャ暗号化の whitelist (include-only) モード。
        // ON のとき、 textureEncryptionIncludeMaterials に列挙した material **だけ** を暗号化し、 それ以外は
        // 全て暗号化 skip する (= leak 許容)。 「頭と体だけ暗号化したい」等、 除外リストに大量列挙する手間を省く。
        // OFF (既定) のときは従来の blacklist (excludeFromTextureEncryption) 動作。
        // exclude リストは include-only でも優先される (include かつ exclude の material は skip = exclude が勝つ)。
        [Tooltip("ON にすると、下のリストに入れたマテリアルだけテクスチャ暗号化します (ホワイトリスト方式)。「頭と体だけ暗号化したい」等で、除外リストに大量入力する手間を省けます。\n" +
                 "重要: リストに無いマテリアルのテクスチャは暗号化されず AssetBundle に残ります (抽出可能)。\n" +
                 "OFF (既定) では、除外リスト以外を全て暗号化します。")]
        [SerializeField] private bool textureEncryptionIncludeOnly = false;

        [Tooltip("ホワイトリスト方式が ON のとき、暗号化するマテリアルをここに列挙します。\n" +
                 "ここに無いマテリアルのテクスチャは暗号化されません (メッシュ保護は別トグルのまま効きます)。")]
        [SerializeField] private Material[] textureEncryptionIncludeMaterials = new Material[0];

        // ────────────────────────────── Texture Pixel Encryption (v0.31, 実験的) ──────────────────────────────

        [Tooltip("lilToon マテリアルの主要テクスチャを暗号化し、AssetBundle 内のテクスチャをノイズ化します。正しい鍵が送られたときだけアバター内部で復号して表示します。抜き取った画像はノイズにしか見えません (既定 OFF、実験的)。\n" +
                 "対応は lilToon のみ。1 メッシュに複数マテリアルがある場合やセーフティ表示中はノイズのまま表示されます (仕様)。\n" +
                 "暗号化した画像は圧縮が効かないため、ダウンロード容量が大きく増えます。ビルド時に容量を見積もり、VRChat の上限を超えるときは警告し、確実に超える場合はアップロードを止めます。")]
        [SerializeField] private bool enableTexturePixelEncryption = false;

        [Tooltip("テクスチャ暗号化が ON のとき有効: メインの色テクスチャ (_MainTex) を暗号化します (透明部分の縁を保つため不透明度には手を付けません)。")]
        [SerializeField] private bool encryptMainTex = true;

        [Tooltip("テクスチャ暗号化が ON のとき有効: 法線マップ (_NormalMap) を暗号化します。")]
        [SerializeField] private bool encryptNormalMap = true;

        [Tooltip("テクスチャ暗号化が ON のとき有効: 2nd カラーレイヤー (_Main2ndTex) を暗号化します。")]
        [SerializeField] private bool encryptMain2nd = true;

        [Tooltip("テクスチャ暗号化が ON のとき有効: 透明度マスク (_AlphaMask) を暗号化します。")]
        [SerializeField] private bool encryptAlphaMask = true;

        // v0.31.14 revert: encryptEmissionMap toggle は v0.31.13 で導入したが、
        // OVERRIDE_EMISSION_1ST inline 展開が複数 lilToon variant で compile error
        // (`fd.invLighting` / `fd.albedo` / `lilCalcBlink` 等が未定義) → 全 renderer 元 shader fallback
        // → 全 texture 露出という致死 regression を起こしたため、 spec entry / 専用 builder と一緒に撤去。
        // serialized field 自体も削除 (= v0.31.13 で保存された値は次の Save Project で消える、 機能無効のため無害)。

        [Tooltip("暗号化するテクスチャの最大解像度 (長辺、px)。これを超えるテクスチャは縮小してから暗号化し、AssetBundle 容量の増加を抑えます。\n" +
                 "・既定 2048: 1K/2K はそのまま、4K のみ 2K に縮小。\n" +
                 "・容量目安: 4K = 64 MB、2K = 16 MB、1K = 4 MB。")]
        [Range(64, 8192)]
        [SerializeField] private int textureEncryptionMaxResolution = 2048;

        /// <summary>画像ごとの暗号化設定。Editor 側で画像の識別子を解決して使用する。</summary>
        [SerializeField] private TextureOverride[] textureOverrides = new TextureOverride[0];

        // ── v0.34.7: Fallback shader 用 placeholder (= VRChat Safety で shader fallback 中の他 user に低解像度 preview を見せる) ──
        // UI からは削除済み。過去のシーンデータを読み込むためフィールドだけ残し、値は参照しない。
        [Tooltip("VRChat のセーフティでシェーダーが無効化された相手には、低解像度のぼかし画像を表示します。正規に解錠している相手の見た目には影響しません。")]
#pragma warning disable 0414
        [SerializeField] private bool showFallbackPlaceholder = true;
#pragma warning restore 0414

        [Tooltip("ぼかし画像の解像度 (px)。\n" +
                 "・16: モザイク状 (保護が最も強い)\n" +
                 "・64 (既定): ぼやけたシルエット\n" +
                 "・256: ほぼ判別可能 (保護効果は薄い)")]
        [Range(16, 256)]
        [SerializeField] private int fallbackPlaceholderResolution = 64;

        // ── v0.34.0+: Universal LIL_SAMPLE_* wrapper category groups ──

        [Tooltip("各種のマスクテクスチャ (ブレンド・ディゾルブ・アウトライン幅・影・ファーなど約 17 種) をまとめて暗号化します。\n" +
                 "メタリック・滑らかさ・視差マップなどは構造上の都合で対象外です。既定 ON。")]
        [SerializeField] private bool encryptMaskGroup = true;

        [Tooltip("各種の色テクスチャ (アウトライン・MatCap・影・逆光・反射・ラメ・グラデーションなど約 9 種) をまとめて暗号化します。\n" +
                 "顔のデカール (2nd / 3rd レイヤー) は構造上の都合で対象外です。既定 ON。")]
        [SerializeField] private bool encryptColorGroup = true;

        [Tooltip("各種の法線マップ (2nd・MatCap 用・アウトライン用・ファー用など約 6 種) をまとめて暗号化します。既定 ON。")]
        [SerializeField] private bool encryptNormalGroup = true;

        [Tooltip("発光テクスチャ (約 5 種) をまとめて暗号化します。\n" +
                 "全 lilToon バリエーションでの検証が終わるまでは、念のため OFF を推奨します。既定 OFF。")]
        [SerializeField] private bool encryptEmissionGroup = false;

        // ── v0.34.15 (案 Y): lilToon カスタム派生 shader 互換 mode ──

        [Tooltip("lilToon をベースにした特殊シェーダー (公式 lilToon そのものではなく改造・拡張版) を使うマテリアルを、保護の対象から完全に外します。\n" +
                 "例: BoundBonePro の lilToonSquish、lilToon の「カスタムシェーダー作成」で作ったもの、他ツールが作った lilToon 派生。\n" +
                 "これらは元のまま維持され、見た目も機能も保たれます。その代わりテクスチャは暗号化されず抜き取り可能になります。公式 lilToon と Poiyomi は通常どおり暗号化されます。\n" +
                 "既定 ON (特殊シェーダー使用時に真っ白になるのを防ぎます)。OFF にすると該当マテリアルが真っ白に表示されることがあります。")]
        [SerializeField] private bool skipCustomLilToonDerivatives = true;

        // ── Emergency disable switches (build-time、 group-level rollback) ──
        [Tooltip("緊急用: Mask グループの暗号化を強制的に無効化します (グループのトグルが ON でも暗号化しません)。既定 OFF。")]
        [SerializeField] private bool disableMaskGroup = false;

        [Tooltip("緊急用: Color グループの暗号化を強制的に無効化します。既定 OFF。")]
        [SerializeField] private bool disableColorGroup = false;

        [Tooltip("緊急用: Normal グループの暗号化を強制的に無効化します。既定 OFF。")]
        [SerializeField] private bool disableNormalGroup = false;

        [Tooltip("緊急用: Emission グループの暗号化を強制的に無効化します。既定 OFF。")]
        [SerializeField] private bool disableEmissionGroup = false;

        [Tooltip("暗号化していないテクスチャへの参照を保護版マテリアルから取り除き、抜き取られないようにします。\n" +
                 "発光・MatCap・アウトライン・リム・2nd・影・ディテールなど見た目に重要なものは、品質維持のため残します。\n" +
                 "既定 ON。OFF にすると全テクスチャ参照がマテリアルに残ります。")]
        [SerializeField] private bool stripUnencryptedTextureRefs = true;

        [Tooltip("VRCFury がアバター実行時に生成するマテリアル/シェーダーは、ビルド時の暗号化が効かず、テクスチャが抜き取られる可能性があります。\n" +
                 "既定 OFF: VRCFury を検出すると警告を表示します (ビルドは続行)。ON にするとその点を承知したものとして警告を出しません。")]
        [SerializeField] private bool acknowledgeVRCFuryLeak = false;

        [Tooltip("ビルド時に Renderer の GameObject 名をランダムな文字列に置き換えます。抜き取られたアバターで、どれが顔・髪・服かを分かりにくくします。\n" +
                 "Humanoid ボーンは対象外です (アバターが壊れるのを回避)。\n" +
                 "副作用: Hierarchy / Inspector で対象を追いにくくなります。")]
        [SerializeField] private bool enableGameObjectObfuscation = false;

        [Tooltip("ビルド時に BlendShape (シェイプキー) の名前をランダムな文字列に置き換えます。順序は維持され、アニメーションや各種ツールからの参照も自動で同期されるためギミックは壊れません。\n" +
                 "抜き取り時に「Smile_L」「vrc.v_aa」等の意味のある名前が消えます。")]
        [SerializeField] private bool enableBlendShapeObfuscation = false;

        [Tooltip("BlendShape 難読化が ON のとき、MMD ワールド用の標準モーフ (あ / い / う / まばたき等) を名前の置き換えから除外します (順序シャッフルには参加します)。\n" +
                 "MMD ワールドは BlendShape 名で表情を動かすため、除外しないと表情が動かなくなります。名前に「MMD」を含む区切り行も自動で除外されます。既定 ON。")]
        [SerializeField] private bool excludeMmdBlendShapes = true;

        [Tooltip("ビルド時にアニメーターのレイヤー名・ステート名・パラメーター名をランダムな文字列に置き換え、参照も自動で同期します。\n" +
                 "VRChat が必須とするパラメーター (Viseme / Gesture / IsLocal 等) と解錠用パラメーターは対象外なので、機能には影響しません。")]
        [SerializeField] private bool enableAnimatorObfuscation = false;

        [Tooltip("パラメーター難読化が ON のとき、VRCOSC (心拍計・音声認識などの外部 OSC アプリ) が使うパラメーター (名前が「VRCOSC/」で始まるもの) を置き換えから除外します。\n" +
                 "除外しないと心拍計などが機能停止します。既定 ON。")]
        [SerializeField] private bool excludeVrcOscParameters = true;

        [Tooltip("ON にするとパラメーター難読化を再現可能にします。同じ元の名前は常に同じ難読名になり、PC と Quest を別々にビルドしても一致します。\n" +
                 "パラメーター難読化が ON のときのみ効果があります。既定 OFF (ビルドごとにランダム)。")]
        [SerializeField] private bool deterministicObfuscation = false;

        [Tooltip("再現可能な難読化のためのシード (32 文字の hex)。トグル ON 時に自動生成されます。\n" +
                 "PC/Quest を別々の prefab で作る場合は、両方に同じ値を設定してください。")]
        [SerializeField] private string obfuscationSeedHex = "";

        [Tooltip("ビルド時に、アバターが参照するメッシュ・マテリアル・アニメーションなどのアセット名をランダムな文字列に置き換えます。抜き取り時に「Mesh_Body」「Smile_Anim」等の意味のある名前が消えます。\n" +
                 "元のアセットは複製してから変更するので壊れません。シェーダーとテクスチャは対象外です (保護を壊さないため)。")]
        [SerializeField] private bool enableAssetNameObfuscation = false;

        [Tooltip("ビルド時に、各メッシュにダミーの BlendShape をランダムな数 (32〜64 個) 追加します。抜き取られたメッシュで、本物の Viseme・表情の BlendShape が意味不明なダミーに紛れて分かりにくくなります。\n" +
                 "本物の名前・順序・index は完全に保存するため、アニメーションや各種ツールの参照は一切壊れません。\n" +
                 "副作用: メッシュのファイルサイズが少し増えます。")]
        [SerializeField] private bool enableBlendShapeDecoy = false;

        [Tooltip("Decoy として追加する dummy BlendShape の最低個数。 ビルドごとにこの値〜MaxCount の間でランダム決定される。")]
        [Range(8, 128)]
        [SerializeField] private int blendShapeDecoyMinCount = 32;

        [Tooltip("Decoy として追加する dummy BlendShape の最大個数。 ビルドごとに MinCount〜この値の間でランダム決定される。")]
        [Range(8, 128)]
        [SerializeField] private int blendShapeDecoyMaxCount = 64;

        // ────────────────────────────── Decoy Animator (v0.28) ──────────────────────────────

        [Tooltip("解析を惑わすため、ダミーの SkinnedMeshRenderer・マテリアル・アニメーターをアバターに追加します。本物の解錠処理とダミーが見分けにくくなります。\n" +
                 "ダミーは同期パラメーターを消費しません。\n" +
                 "副作用: Hierarchy やアセット一覧にダミーが多数並び、Performance Rank に影響する場合があります。")]
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

        [Tooltip("アバター配下のオブジェクトの並び順をランダムに入れ替えます。抜き取り時に、直感的な構造から組成を推測しにくくします。\n" +
                 "腕・脚のボーンや PhysBone など並び順に依存する部分は並び替えません。VRCFury を使っているアバターでは並び替えません。\n" +
                 "注意: Modular Avatar のメニュー順は並び替えで乱れることがあります。下の設定 (既定 ON) で防げます。")]
        [SerializeField] private bool enableHierarchyShuffle = false;

        [Tooltip("Hierarchy 並び替えが ON のとき、Modular Avatar のメニュー項目を持つ親の並び替えを除外し、メニューの順序を保ちます。\n" +
                 "MA はオブジェクトの並び順でメニュー項目順を決めるため、除外しないとメニューが乱れます。既定 ON。")]
        [SerializeField] private bool preserveMaMenuOrder = true;

        // ────────────────────────────── 対象別絞り込み (v0.42) ──────────────────────────────
        // #2/#3/#5: 難読化の「対象」をユーザーが個別に絞り込む (= blacklist 除外) 統合設定。
        // 既存の excludeFromShaderLock / excludeFromTextureEncryption / MmdBlendShapeWhitelist と
        // 同じ「全対象にかけて個別に外す」設計に揃える。

        [Tooltip("GameObject 名の難読化から除外する GameObject を指定します。指定した GameObject は元の名前のまま維持されます。\n" +
                 "用途: 名前やパスで参照される外部連携・OSC・デバッグ対象など。\n" +
                 "GameObject 名難読化が ON のときのみ効果があります。")]
        [SerializeField] private GameObject[] obfuscationExcludeGameObjects = new GameObject[0];

        [Tooltip("v0.42+: ON のとき、 上で指定した GameObject の子孫 (子・孫…) も全て GO 名難読化から除外する。\n" +
                 "OFF のときは指定した GameObject 自身のみ除外する。\n" +
                 "default ON (= 衣装ルートを 1 つ指定すれば配下メッシュ全部を除外、という最頻ユースケースに合わせる)。")]
        [SerializeField] private bool obfuscationExcludeIncludeChildren = true;

        [Tooltip("BlendShape 難読化が ON のとき、フェイストラッキング (VRCFaceTracking / ARKit 等) の標準 BlendShape 名を名前の置き換えから除外します (順序シャッフルには参加します)。\n" +
                 "フェイストラッキングは BlendShape 名で駆動するため、除外しないと連携が切れます。既定 ON。")]
        [SerializeField] private bool excludeFaceTrackingBlendShapes = true;

        [Tooltip("BlendShape 難読化から手動で除外する BlendShape 名 (大文字小文字を区別しない完全一致)。\n" +
                 "フェイストラッキングのプリセットで拾えない、作者独自の名前を救済したいときに使います。指定した BlendShape は元の名前のまま残ります。")]
        [SerializeField] private string[] extraBlendShapesToExclude = new string[0];

        [Tooltip("フェイストラッキング用のパラメーター (名前が「v2/」で始まる等) をパラメーター難読化から除外します。\n" +
                 "VRCFaceTracking 等は名前で OSC 送信するため、除外しないと顔トラッキングが機能停止します。既定 ON。")]
        [SerializeField] private bool excludeFaceTrackingParameters = true;

        [Tooltip("パラメーター難読化から手動で除外するパラメーター名。\n" +
                 "なでなで等の接触ギミックは自動で保護されますが、自動で拾えなかった場合の救済用です。\n" +
                 "完全一致のほか、末尾を「/」にすると前方一致になります (例: 'MyGimmick/')。指定したパラメーターは元の名前のまま残ります。")]
        [SerializeField] private string[] excludeParameterNamesFromObfuscation = new string[0];

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
        [Tooltip("生成したシェーダーをビルド間で保持し、2 回目以降のビルドを高速化します (既定 ON、多マテリアルのアバターで数十分→数分)。\n" +
                 "ただし保護シェーダーがプロジェクト内に残るため、解析者にシェーダー構造を見られる可能性があります (テクスチャの復号には解錠が別途必要なため、シェーダーだけでは復号できません)。\n" +
                 "解析耐性を最大化したい配布時のみ OFF にできます。")]
        [SerializeField] private bool keepGeneratedShadersBetweenBuilds = true;

        // ────────────────────────────── プロパティ ──────────────────────────────

        public string CreatorName => creatorName;
        public string LicenseUrl => licenseUrl;
        public string ContactInfo => contactInfo;

        public bool EnableAssetWatermark => enableAssetWatermark;
        public bool EnableHierarchyWatermark => enableHierarchyWatermark;
        public bool EnableBuildFingerprint => enableBuildFingerprint;
        public bool ShowBuildReportInNdmf => showBuildReportInNdmf;

        public bool EnableMeshLock => enableMeshLock;
        // v0.49: メッシュ崩しの強さは 0.1m 固定 (設定 UI から撤去)。 旧 meshLockScrambleRadius 直列化フィールドは廃止。
        //   MeshLockPass の BlendShape 頂点 scramble 半径 / ShaderLockPass の UV displacement magnitude (×2.0) の基準値。
        public float MeshLockScrambleRadius => 0.1f;
        public bool MeshLockKeySaved => meshLockKeySaved;
        public bool AutoExcludeSpsDpsFromMeshLock => autoExcludeSpsDpsFromMeshLock;
        public bool AutoExcludePlugFromShaderLock => autoExcludePlugFromShaderLock;
        public string MeshLockKeyHex => meshLockKeyHex;
        // v0.9: 16 文字 hex (8 byte / 64 bit)。旧形式 (8 文字 hex / 32 bit) は無効扱い → 再生成必要
        public bool HasMeshLockKey => !string.IsNullOrEmpty(meshLockKeyHex) && meshLockKeyHex.Length == 16;
        public const int KeyByteCount = 8;

        /// <summary>
        /// テクスチャ復号鍵 TK の byte 数。 PackKeyBytes が下位 4 byte を使うため 4 (= K0..K3 に対応)。
        /// </summary>
        public const int TextureKeyByteCount = 4;

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
        public int TextureEncryptionMaxResolution => SnapTextureResolution(textureEncryptionMaxResolution);

        /// <summary>画像ごとの暗号化設定を返す。未設定時は空配列を返す。</summary>
        public TextureOverride[] TextureOverrides => textureOverrides ?? new TextureOverride[0];

        // v0.34.7: Fallback placeholder は常に有効 (旧 serialized 値は互換性のため保持)。
        public bool ShowFallbackPlaceholder => true;
        public int FallbackPlaceholderResolution => SnapPlaceholderResolution(fallbackPlaceholderResolution);
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

        // v0.42+: Hierarchy Shuffle 時に MA メニュー (Menu Item / Group) の sibling 順を保持する (default ON)
        public bool PreserveMaMenuOrder => preserveMaMenuOrder;

        // ────────────────────────────── 対象別絞り込み (v0.42) accessors ──────────────────────────────

        // #2: GO 名難読化の除外対象 (子含むフラグ付き)
        public GameObject[] ObfuscationExcludeGameObjects => obfuscationExcludeGameObjects ?? new GameObject[0];
        public bool ObfuscationExcludeIncludeChildren => obfuscationExcludeIncludeChildren;

        // #3: FT BlendShape 除外トグル + 手動追加リスト
        public bool ExcludeFaceTrackingBlendShapes => excludeFaceTrackingBlendShapes;
        public string[] ExtraBlendShapesToExclude => extraBlendShapesToExclude ?? new string[0];

        // #4/#5: パラメータ難読化の手動除外リスト
        public string[] ExcludeParameterNamesFromObfuscation => excludeParameterNamesFromObfuscation ?? new string[0];

        // FT パラメータ (v2/ prefix 等) をパラメータ名難読化から自動除外するトグル
        public bool ExcludeFaceTrackingParameters => excludeFaceTrackingParameters;

        /// <summary>
        /// v0.42 (#2): GO 名難読化から除外する Transform を集約して <paramref name="into"/> に追加する。
        /// 各除外 GameObject 自身を追加し、 <see cref="ObfuscationExcludeIncludeChildren"/> が true なら
        /// その子孫 Transform も全て追加する。
        /// avatar 外 / 別 avatar 配下に誤指定された GO は無視する (avatarRoot 配下のみ採用)。
        /// GameObjectObfuscationPass の protectedTransforms にこの集合を合流させることで、
        /// 既存の protectedTransforms.Contains(t) チェックがそのまま除外 GO (+子) を rename からスキップする。
        /// </summary>
        public void CollectObfuscationExcludedTransforms(Transform avatarRoot, HashSet<Transform> into)
        {
            if (into == null || obfuscationExcludeGameObjects == null) return;
            for (int i = 0; i < obfuscationExcludeGameObjects.Length; i++)
            {
                var go = obfuscationExcludeGameObjects[i];
                if (go == null) continue;
                var t = go.transform;
                // stray reference ガード: avatarRoot 配下 (= 自身 or 子孫) のみ採用
                if (avatarRoot != null && t != avatarRoot && !t.IsChildOf(avatarRoot)) continue;
                into.Add(t);
                if (obfuscationExcludeIncludeChildren)
                {
                    var children = go.GetComponentsInChildren<Transform>(true);
                    for (int c = 0; c < children.Length; c++) into.Add(children[c]);
                }
            }
        }

        /// <summary>
        /// v0.42 (#3/#5): BlendShape 名が手動除外リスト (<see cref="ExtraBlendShapesToExclude"/>) に
        /// 含まれるか判定する (case-insensitive 完全一致)。
        /// FT プリセット (FaceTrackingBlendShapeWhitelist) との OR 結合は Editor 側
        /// (BlendShapeObfuscationPass) で行う (Runtime は Editor whitelist を参照できないため)。
        /// </summary>
        public bool IsBlendShapeManuallyExcluded(string blendShapeName)
        {
            if (string.IsNullOrEmpty(blendShapeName) || extraBlendShapesToExclude == null) return false;
            for (int i = 0; i < extraBlendShapesToExclude.Length; i++)
            {
                var n = extraBlendShapesToExclude[i];
                if (string.IsNullOrEmpty(n)) continue;
                if (string.Equals(n, blendShapeName, System.StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// v0.42 (#4/#5): パラメータ名が手動除外リスト (<see cref="ExcludeParameterNamesFromObfuscation"/>) に
        /// 含まれるか判定する。 完全一致 (case-sensitive) が基本。 末尾が '/' のエントリは prefix 一致。
        /// AnimatorObfuscationPass が paramRenameMap 構築時にこれを呼び、 true なら rename しない。
        /// </summary>
        public bool IsParameterObfuscationExcluded(string parameterName)
        {
            if (string.IsNullOrEmpty(parameterName) || excludeParameterNamesFromObfuscation == null) return false;
            for (int i = 0; i < excludeParameterNamesFromObfuscation.Length; i++)
            {
                var n = excludeParameterNamesFromObfuscation[i];
                if (string.IsNullOrEmpty(n)) continue;
                if (n.EndsWith("/", System.StringComparison.Ordinal))
                {
                    if (parameterName.StartsWith(n, System.StringComparison.Ordinal)) return true;
                }
                else if (string.Equals(n, parameterName, System.StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// パラメータ名が VRCFaceTracking (Unified Expressions) の外部 OSC 駆動パラメータかを判定する。
        /// VRCFaceTracking は unified expressions を 'v2/JawOpen' の様に 'v2/' prefix で送信し、
        /// 組織 prefix 付き ('MyOrg/v2/JawOpen' 等) の形も存在する。 加えて legacy 3 名
        /// (EyeTrackingActive / LipTrackingActive / ExpressionTrackingActive) も外部駆動される。
        /// これらは外部アプリが名前で OSC 送信するため、 難読化 rename すると顔トラが機能停止する。
        /// 照合: 'v2/' 前方一致 ∨ '/v2/' 部分一致 ∨ legacy 3 名との完全一致 (いずれも Ordinal)。
        /// null / 空文字は false。
        /// </summary>
        public static bool IsFaceTrackingParameter(string parameterName)
        {
            if (string.IsNullOrEmpty(parameterName)) return false;
            if (parameterName.StartsWith("v2/", System.StringComparison.Ordinal)) return true;
            if (parameterName.IndexOf("/v2/", System.StringComparison.Ordinal) >= 0) return true;
            if (string.Equals(parameterName, "EyeTrackingActive", System.StringComparison.Ordinal)) return true;
            if (string.Equals(parameterName, "LipTrackingActive", System.StringComparison.Ordinal)) return true;
            if (string.Equals(parameterName, "ExpressionTrackingActive", System.StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// 与えられた shader が AntiRipping の lock 対象とみなせるかを判定する。
        /// 自動対象: lilToon 派生 / Poiyomi 派生 (".poiyomi/..." または "Poiyomi" を含む)。
        /// 加えて <c>ExtraShaderNamesToLock</c> に指定された shader 名 (部分一致、大文字小文字無視) も対象。
        /// v0.33.9+: <c>ExcludeFromShaderLock</c> に指定された shader 名 (部分一致) は強制的に対象外にする
        /// (lilToon カスタムシェーダーで pink バグが起きるケースの override)。
        /// </summary>
        public bool ShouldLockShader(Shader shader)
        {
            return MatchesLockTargetShader(shader) && !MatchesShaderLockExcludeList(shader);
        }

        /// <summary>
        /// shader 名が lock 対象 (自動: lilToon 派生 / Poiyomi 派生、 加えて <c>ExtraShaderNamesToLock</c> の
        /// 部分一致) に該当するかだけを判定する。 除外リストは考慮しない。
        /// null / 空白の shader 名は false。 <see cref="ShouldLockShader"/> の match 判定を切り出したもの。
        /// </summary>
        private bool MatchesLockTargetShader(Shader shader)
        {
            if (shader == null || string.IsNullOrEmpty(shader.name)) return false;

            // lilToon 派生は無条件で対象
            if (shader.name.IndexOf("lilToon", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // Poiyomi 派生も自動対象 (".poiyomi/Poiyomi Toon" 等、Thry-locked 変種 "Hidden/Locked/.poiyomi/..." も拾う)
            if (shader.name.IndexOf(".poiyomi/", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (shader.name.IndexOf("Poiyomi", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // ユーザー指定の追加対象
            foreach (var s in ExtraShaderNamesToLock)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (shader.name.IndexOf(s, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// v0.33.9+: shader 名が手動 exclude リスト (<see cref="ExcludeFromShaderLock"/>) に部分一致するかを判定する。
        /// IsNullOrWhiteSpace のエントリは skip、 照合は IndexOf OrdinalIgnoreCase。
        /// lilToon カスタムシェーダー (BoundBonePro lilToonSquish 等) で pink バグが起きる場合の救済 override 用。
        /// </summary>
        private bool MatchesShaderLockExcludeList(Shader shader)
        {
            if (shader == null || string.IsNullOrEmpty(shader.name)) return false;
            foreach (var s in ExcludeFromShaderLock)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (shader.name.IndexOf(s, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// lock 対象 (lilToon/Poiyomi/Extra 一致) だが <c>excludeFromShaderLock</c> で除外された shader のみ true。
        /// 非対応 shader は false (集計の偽陽性防止)。
        /// 「lock 対象だが手動除外された」ケースを外部集計コードが正しく判別するための理由付き API。
        /// </summary>
        public bool IsShaderLockManuallyExcluded(Shader shader)
        {
            return MatchesLockTargetShader(shader) && MatchesShaderLockExcludeList(shader);
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

        // v0.42: 解錠中の heartbeat (synced bool) param 名。空時 (旧データ) は固定名フォールバック。
        public string GetHeartbeatParamName() =>
            string.IsNullOrEmpty(meshLockHeartbeatParamName) ? "_AjisaiAR_Heartbeat" : meshLockHeartbeatParamName;

        public string[] MeshLockTextureKeyParamNames => meshLockTextureKeyParamNames ?? new string[0];

        public bool HasMeshLockTextureKeyParamNames =>
            meshLockTextureKeyParamNames != null
            && meshLockTextureKeyParamNames.Length == TextureKeyByteCount
            && System.Array.TrueForAll(meshLockTextureKeyParamNames, n => !string.IsNullOrEmpty(n));

        // v0.43: テクスチャ鍵 TK_i の synced param 名。 未生成 (旧データ) は固定名フォールバック。
        public string GetTextureKeyParamName(int index)
        {
            if (index < 0 || index >= TextureKeyByteCount) return null;
            if (HasMeshLockTextureKeyParamNames) return meshLockTextureKeyParamNames[index];
            return $"_AjisaiAR_TK{index}";
        }

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

        public Texture2D[] ExcludeTexturesFromEncryption => excludeTexturesFromEncryption ?? new Texture2D[0];

        /// <summary>
        /// v0.51: src texture が ExcludeTexturesFromEncryption list に含まれているか判定 (テクスチャ単位除外)。
        /// 含まれていれば texture pixel encryption を skip する (= 平文で AssetBundle に焼かれ leak 許容、 shader-lock /
        /// mesh 保護は不変)。 元 (src) texture asset の reference 比較で、 prefab material に貼られている original texture
        /// のみマッチする (= clone / 暗号化済 variant は別 reference)。 引数は Texture 型で受け、 material.GetTexture の
        /// 戻り値をそのまま渡せるようにする (実体が Texture2D 以外でも reference 比較で自然に false になる)。
        /// </summary>
        public bool IsTextureExcludedFromEncryption(Texture tex)
        {
            if (tex == null || excludeTexturesFromEncryption == null || excludeTexturesFromEncryption.Length == 0)
                return false;
            for (int i = 0; i < excludeTexturesFromEncryption.Length; i++)
            {
                if (excludeTexturesFromEncryption[i] == tex) return true;
            }
            return false;
        }

        // v0.42: include-only (whitelist) モード。
        public bool TextureEncryptionIncludeOnly => textureEncryptionIncludeOnly;
        public Material[] TextureEncryptionIncludeMaterials => textureEncryptionIncludeMaterials ?? new Material[0];

        /// <summary>
        /// v0.42: include-only モードにより src material が暗号化 skip 対象か判定する。
        /// モード OFF のときは常に false (この規則では skip しない)。
        /// モード ON のときは include リストに含まれない material を skip 対象 (true) とする
        /// (リストが空なら全 material が skip = 暗号化されない)。
        /// exclude リスト (IsTextureEncryptionExcluded) とは独立で、 呼び出し側で OR して使う (exclude が勝つ)。
        /// reference 比較で、 prefab に貼られている original material のみマッチする。
        /// </summary>
        public bool IsTextureEncryptionSkippedByIncludeOnly(Material srcMat)
        {
            if (!textureEncryptionIncludeOnly) return false;
            if (srcMat == null) return false; // null は他経路で扱う。 ここでは skip 判定しない
            var list = textureEncryptionIncludeMaterials;
            if (list == null) return true; // include-only ON かつ list 未設定 → 全て skip
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] == srcMat) return false; // include リストにある → skip しない (= 暗号化する)
            }
            return true; // include-only ON かつ list に無い → skip (= 暗号化しない)
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
