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

        /// <summary>
        /// 暗号化の形式。0 = 自動 (圧縮を保つ設定が ON のとき、条件に合う画像は圧縮したまま暗号化する)、
        /// 1 = 無圧縮で暗号化する (従来どおり)。圧縮を保つ設定が OFF のときは、どちらでも無圧縮で暗号化する。
        /// </summary>
        public int format;
    }

    /// <summary>
    /// 「暗号化するテクスチャの種類」ウィンドウで適用した、1 プロパティぶんの ON/OFF 記録。設計書 §3.1。
    /// Runtime は Editor の catalog を参照できないため、ここはデータの読み書きだけを持つ。
    /// </summary>
    [System.Serializable]
    public sealed class TexturePropertyState
    {
        /// <summary>シェーダー系統。"lilToon" または "Poiyomi"。</summary>
        public string family;

        /// <summary>shader property 名 (例: "_MainTex")。</summary>
        public string property;

        /// <summary>この用途を暗号化するかどうか。</summary>
        public bool enabled;
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

        [Tooltip("透かしに記録する作者名・ハンドル名。入力は必須です。")]
        [SerializeField] private string creatorName = "";

        [Tooltip("ライセンスURLやBOOTHの商品ページ。入力は任意ですが、推奨します。")]
        [SerializeField] private string licenseUrl = "";

        [Tooltip("作者の連絡先。XのURL、Discord ID、メールアドレスなどを入力します。任意です。")]
        [SerializeField] private string contactInfo = "";

        // ────────────────────────────── 追跡保護 (v0.1) ──────────────────────────────

        [Tooltip("全マテリアルにこのビルド固有の作者ハッシュを埋め込みます。見た目や動作は変わりません。\n" +
                 "流出したマテリアルをレポート (Logs~ フォルダ) の記録と照合し、自分のビルドから出たものだと証明できます。")]
        [SerializeField] private bool enableAssetWatermark = true;

        [Tooltip("作者情報を含む非表示のGameObjectを、アバター階層の複数箇所に配置します。")]
        [SerializeField] private bool enableHierarchyWatermark = true;

        [Tooltip("ビルドごとに固有IDを生成し、流出時の追跡に使えるレポートをAssets/紫陽花広場/anti-ripping/Logs~/ に書き出す")]
        [SerializeField] private bool enableBuildFingerprint = true;

        [Tooltip("ビルド後に、難読化の結果・失敗・スキップした項目をまとめたレポートウィンドウを自動で開きます。")]
        [SerializeField] private bool showBuildReportInNdmf = true;

        // ────────────────────────────── 表示阻止 (v0.2) ──────────────────────────────
        // v0.3 で撤廃した enableMeshLock トグルを v0.34.20 で再導入。
        // スコープは MeshLockPass の BlendShape 頂点 scramble のみ。
        // shader-level decode (enableShaderLevelDecode) / texture 暗号化 (enableTexturePixelEncryption) /
        // KeyAnimator / OSC sender / Unlock manifest は独立に動作する。
        [Tooltip("鍵が無いときにメッシュの頂点を散らして形を崩し、正しい鍵で元に戻します。\n" +
                 "OFFにすると頂点を散らす処理だけを省略します。シェーダー復号・テクスチャ暗号化・解錠処理は、それぞれの設定で切り替えます。\n" +
                 "頂点を散らすことでアバターの大きさ判定が増えることはなくなりますが、メッシュロックの保護は弱くなります。")]
        [SerializeField] private bool enableMeshLock = true;

        [Tooltip("PC向けのメッシュ暗号化を使用します。座標・法線・接線・表情差分を64bit鍵で暗号化します。\n" +
                 "対応シェーダーと更新された解錠アプリが必要です。非対応の構成はビルド時に停止します。\n" +
                 "テクスチャ暗号化のON/OFFにかかわらず同期予算は16bitです。自分は鍵が届くと復号しますが、他の人には全鍵の分割同期が完了してから表示されます。\n" +
                 "鍵と検査値を詰めて送り、共通鍵の通常再送はテクスチャ併用でも一巡約9秒です。独立したテクスチャ鍵を使う互換設定では約11秒です。初回は0.1秒間隔でも先行送信します。検査時間や通信・読み込み時間が加わり、表示までの時間や上限は保証しません。")]
        [SerializeField] private bool meshKeyEncryption = true;

        // The independent legacy key is retained for format2/3 compatibility.
        // Shared format4/5 uses all eight drawing-key bytes for both features.
        [SerializeField, HideInInspector] private string meshDrawingKeyHex = "";

        [Tooltip("メッシュ暗号化とテクスチャ暗号化で64bitの解錠鍵を共通化します。同期する鍵を1本に減らします。OFFは従来の独立したテクスチャ鍵を使います。変更後は再ビルド・再アップロードしてください。")]
        [SerializeField] private bool shareMeshAndTextureKey = true;

        [Tooltip("ON: OSCで鍵を1回送信するとVRChatのパラメーターに保存し、次回以降は自動で復元します。推奨設定です。\n" +
                 "OFF: VRChatを起動するたびに、AntiRippingClientからOSCで鍵を送る必要があります。より安全です。")]
        [SerializeField] private bool meshLockKeySaved = true;

        [Tooltip("ON (既定): SPS / DPS / TPSなど、シェーダーで頂点を変形するplugメッシュを検出し、メッシュロックの頂点散らしから自動で除外します。\n" +
                 "これらは頂点を置き換えると正常に変形しなくなるため、初期設定では除外します。対象GameObjectのAnti-Ripping Scope Overrideでも個別に調整できます。\n" +
                 "注意: 除外されたplugはメッシュロックで保護されません (SPSとは原理的に両立できません)。")]
        [SerializeField] private bool autoExcludeSpsDpsFromMeshLock = true;

        [Tooltip("ON (既定): 検出したSPS / DPS / TPS plugをシェーダーロックとテクスチャ暗号化からも自動で除外します。\n" +
                 "VRCFuryはビルド時にplugのシェーダーを書き換えるため、保護シェーダーが対象になるとTPSはビルド失敗、SPSは表示不具合のリスクがあります。\n" +
                 "plugが本体とマテリアルを共有している場合は、本体のテクスチャを暗号化せずに出力しないよう、除外しません。\n" +
                 "注意: 除外されたplugのマテリアル・テクスチャは保護されません。")]
        [SerializeField] private bool autoExcludePlugFromShaderLock = true;

        [Tooltip("16文字の16進数で表す解錠キー。Inspectorの「鍵を今すぐ作成」ボタンで生成します。未生成の場合はビルド時に自動生成します。\n" +
                 "ビルド時に鍵が空の場合はメッシュロックを省略し、警告ログを出します。")]
        [SerializeField] private string meshLockKeyHex = "";

        [Tooltip("解錠用のOSCパラメーター名8個。鍵の生成時に16文字のランダムな16進数で名前を付けます。\n" +
                 "メッシュ暗号化ではOSC入力を同期しないIntで保持し、2つの同期Intへ分割して他の人へ送信します。従来方式のローカル鍵入力は変わりません。\n" +
                 "解錠には鍵の値と送信先アドレスの両方が必要です。保存済みのOSC名は変更しません。")]
        [SerializeField] private string[] meshLockParamNames = new string[0];

        [Tooltip("即時ロック用のboolパラメーター名。\n" +
                 "Expression Menuの「🔒 Lock Avatar」またはAntiRippingClientの「ロック」ボタンを押すと値が1になり、\n" +
                 "Animator Layer 2 (ForceLock) がBlendShapeのウェイトを0に上書きします。")]
        [SerializeField] private string meshLockNowParamName = "";

        [Tooltip("メッシュ暗号化では、鍵を分割同期する2つのIntチャンネルの1つです。受信前の初期値は0です。\n" +
                 "従来方式では解錠状態を他の人へ送る同期Floatです。鍵そのものはこのFloatに含めません。\n" +
                 "どちらの方式でも、このパラメーターの同期予算は8bitです。")]
        [SerializeField] private string meshLockBroadcastParamName = "";

        [Tooltip("復元用BlendShapeの名前。鍵を生成するときにランダムな名前を付けます。\n" +
                 "従来の固定名_AjisaiAR_Unlockでは、AssetRipperでメッシュを開くだけで\n" +
                 "「このBlendShapeを100にすれば復元される」と分かってしまうため、" +
                 "16文字のランダムな16進数を使い、復元用のBlendShapeを特定しにくくします。")]
        [SerializeField] private string meshLockUnlockBlendShapeName = "";

        [Tooltip("MAの解錠設定を保持するGameObject名。鍵を生成するときにランダムな名前を付けます。\n" +
                 "従来の固定名_AjisaiAR_Lockには、ARC系の保護解除ツールが\n" +
                 "「Lock」で始まる名前から対象を特定できる弱点があるため、16文字のランダムな16進数を使います。\n" +
                 "Hierarchyでは、他のプラグインが生成する_<16hex>形式の名前と見た目を揃えます。")]
        [SerializeField] private string meshLockHolderObjectName = "";

        [Tooltip("従来方式のAAP鍵スコア用の、同期しないFloatパラメーター名です。メッシュ暗号化では使いません。\n" +
                 "Direct Blend TreeがK0..K7からスコアを累積計算し、定数との比較で解錠を判定します。")]
        [SerializeField] private string meshLockScoreParamName = "";

        [Tooltip("AAP用の値を常に1.0に保つfloatパラメーター名。自分のPCでのみ使い、初期値は1です。\n" +
                 "Direct Blend TreeのDirect Blend Paramに指定し、8個の子をすべて有効にします。")]
        [SerializeField] private string meshLockOneParamName = "";

        [Tooltip("メッシュ暗号化では、各クライアント内の0/1表示ヒントを保持する、同期しないFloatパラメーター名です。\n" +
                 "所有者はローカル入力に応じて更新し、受信側は全鍵の分割同期が完了してから更新します。\n" +
                 "従来方式では1 - Broadcastを計算するAAP用Floatで、初期値は1.0です。鍵の生成時にランダムな名前を付けます。")]
        [SerializeField] private string meshLockInvBroadcastParamName = "";

        [Tooltip("メッシュ暗号化では、鍵を分割同期する2つ目のIntチャンネルです。受信前の初期値は0です。\n" +
                 "初回は0.1秒間隔で分割した鍵を高速送信し、その後は1秒間隔で再送します。他の人の画面では全鍵が揃ってから一括で反映します。\n" +
                 "従来方式では、解錠中に所有者が数秒おきに切り替える同期Floatです。途中参加した人にも解錠状態が届くよう再送を促します。")]
        [SerializeField] private string meshLockHeartbeatParamName = "";

        [Tooltip("独立鍵の互換設定で使うテクスチャ復号鍵TK0..3のOSC/Animatorパラメーター名4個です。鍵の生成時に16文字のランダムな16進数で名前を付け、解錠鍵の下位4byteを受け取ります。共通鍵モードはK0..7を使います。\n" +
                 "メッシュ暗号化では同期しないIntで保持し、2チャンネルの分割同期に含めます。従来方式では4つの同期Intで送ります。\n" +
                 "名前が未生成の旧データでは固定名_AjisaiAR_TK0..3を使い、保存済みのOSC名は変更しません。")]
        [SerializeField] private string[] meshLockTextureKeyParamNames = new string[0];

        [Tooltip("各K_iに掛けるソルト値。8byteで、各値は1〜255です。\n" +
                 "score = Σ (K_i × salt_i) / 255で計算し、鍵が一致したときだけexpectedと等しくなります。\n" +
                 "ソルトはクリップ内に隠されるため、解析者は1個のexpected値から8個の鍵を逆算する必要があります。")]
        [SerializeField] private byte[] meshLockSalts = new byte[0];

        [Tooltip("lilToon / Poiyomiのマテリアルを、見た目はそのままに、解錠処理をシェーダー内部で行う保護版に差し替えます (既定ON)。\n" +
                 "lilToonでの役割はテクスチャの復号です (メッシュの散らし・復元はメッシュロックが担当します)。\n" +
                 "対応していないシェーダーのマテリアルはそのまま維持されます。\n" +
                 "テクスチャ暗号化に必要なシェーダー復号は、この互換設定がOFFでも自動で有効になります。")]
        [SerializeField] private bool enableShaderLevelDecode = true;

        [Tooltip("MeshRendererをビルド時にSkinnedMeshRendererへ変換し、メッシュロックで保護できるようにします。\n" +
                 "VRChatのセーフティ (カスタムシェーダー無効化) 時も形状が正しく復元されます。対象はlilToon / Poiyomi系シェーダーを持つMeshRendererのみです。\n" +
                 "副作用: SkinnedMeshRendererが増え、Performance Rankが下がる場合があります (特にQuest)。既定OFF。")]
        [SerializeField] private bool enableMeshRendererToSkinnedConversion = false;

        [Tooltip("自動検出されるlilToon / Poiyomi以外に、保護対象へ含めたいシェーダー名を追加します (一部一致、大文字小文字は無視)。例: 'XSToon'、'Sunao'。\n" +
                 "現在対応しているのはlilToonとPoiyomiのみで、それ以外を指定した場合はメッシュ側の保護に切り替わります。")]
        [SerializeField] private string[] extraShaderNamesToLock = new string[0];

        [Tooltip("シェーダー保護の対象から除外するシェーダー名を指定します (一部一致、大文字小文字は無視)。\n" +
                 "lilToonベースの特殊シェーダーを使ったマテリアルがビルド後にピンク色になる場合、その名前の一部 (例: 'BoundBonePro') を追加すると除外できます。\n" +
                 "除外したマテリアルはメッシュ側の保護のみ適用され、テクスチャは暗号化されません。")]
        [SerializeField] private string[] excludeFromShaderLock = new string[0];

        // v0.37.8: テクスチャ暗号化からマテリアル単位で除外する list。
        // 除外された material は shader-lock は通常通り通る (= mesh-level 保護維持) が、 texture pixel encryption は
        // skip される (= 元 texture が AssetBundle に焼かれて leak 可能、 visual は安定)。
        // 用途: MatCap 等の微調整 texture で暗号化精度損失が visual に影響する material を leak 許容で除外したいケース。
        // 元 (src) material reference で比較する (= avatar prefab に貼られている original material)。
        [Tooltip("テクスチャ暗号化から除外するマテリアルを指定します。除外したテクスチャは抜き取られる可能性があります。\n" +
                 "メッシュの保護は通常どおり適用しますが、テクスチャは暗号化せず元の画像のまま書き出します。\n" +
                 "用途: MatCapなど、暗号化による見た目の変化が気になるマテリアル。\n" +
                 "重要: 指定したマテリアルの全テクスチャが対象です。メインの色テクスチャを含む場合は保護効果が大きく下がるため慎重に選んでください。")]
        [SerializeField] private Material[] excludeFromTextureEncryption = new Material[0];

        // v0.51: テクスチャ (静的 Texture asset) 単位でテクスチャ暗号化から除外する list。
        // material 単位除外 (excludeFromTextureEncryption) が material 内の **全テクスチャ** を外すのに対し、 こちらは
        // material 内の **特定 texture asset 1 枚だけ** を暗号化から外せる (= その texture が使われている他 prop / 他
        // material でも一律 skip される。 元 (src) texture asset の reference で比較)。
        // 除外された texture は平文で AssetBundle に焼かれて leak 許容になるが、 shader-lock / mesh 保護は不変。
        // include-only モードとの関係も material 除外と同じで、 除外は常に優先する (include 指定 material 内でも除外
        // 指定した texture は暗号化しない)。
        // 用途: MatCap・グラデ等、 暗号化の精度損失が visual に出る texture を「1 枚だけ」leak 許容で外したいケース。
        [Tooltip("テクスチャ暗号化から除外する画像を1枚ずつ指定します。除外した画像は抜き取られる可能性があります。\n" +
                 "マテリアル単位の除外と違い、同じマテリアル内でも指定した1枚だけを平文のまま書き出せます。メッシュ・シェーダーの保護は変わりません。\n" +
                 "用途: MatCap・グラデーションなど、暗号化による見た目の変化が気になるテクスチャ。\n" +
                 "ホワイトリスト指定より常に優先されます。")]
        [SerializeField] private Texture[] excludeTexturesFromEncryption = new Texture[0];

        // v0.42: テクスチャ暗号化の whitelist (include-only) モード。
        // ON のとき、 textureEncryptionIncludeMaterials に列挙した material **だけ** を暗号化し、 それ以外は
        // 全て暗号化 skip する (= leak 許容)。 「頭と体だけ暗号化したい」等、 除外リストに大量列挙する手間を省く。
        // OFF (既定) のときは従来の blacklist (excludeFromTextureEncryption) 動作。
        // exclude リストは include-only でも優先される (include かつ exclude の material は skip = exclude が勝つ)。
        [Tooltip("ONにすると、下のリストに入れたマテリアルだけテクスチャ暗号化します (ホワイトリスト方式)。「頭と体だけ暗号化したい」等で、除外リストに大量入力する手間を省けます。\n" +
                 "重要: リストに無いマテリアルのテクスチャは暗号化されずAssetBundleに残ります (抽出可能)。\n" +
                 "OFF (既定) では、除外リスト以外を全て暗号化します。")]
        [SerializeField] private bool textureEncryptionIncludeOnly = false;

        [Tooltip("ホワイトリスト方式がONのとき、暗号化するマテリアルをここに列挙します。\n" +
                 "リストにないマテリアルのテクスチャは暗号化しません。メッシュの保護は別の設定に従って適用します。")]
        [SerializeField] private Material[] textureEncryptionIncludeMaterials = new Material[0];

        // ────────────────────────────── Texture Pixel Encryption (v0.31) ──────────────────────────────

        [Tooltip("対応シェーダーの主要テクスチャを暗号化し、AssetBundle内のテクスチャをノイズ化します。正しい鍵が送られたときだけアバター内部で復号して表示します。抜き取った画像はノイズにしか見えません (既定ON)。\n" +
                 "対応はlilToonと、読み取り方をソースから確認できるPoiyomiの材質テクスチャです。Poiyomiは旧版もバージョンで制限しません。新たに対応した用途は既定OFFです。「暗号化するテクスチャの種類」で選択して有効にします。1メッシュに複数マテリアルがある場合やセーフティ表示中はノイズのまま表示されます (仕様)。\n" +
                 "テクスチャ暗号化でダウンロード容量が増えることがあります。「圧縮したまま暗号化する」に対応する画像は展開後の容量を減らせますが、ダウンロード容量は画像と形式によって変わります。容量はビルド時に見積もり、上限を超えそうなら警告し、確実に超える場合はアップロードを止めます。")]
        [SerializeField] private bool enableTexturePixelEncryption = true;

        [Tooltip("テクスチャ暗号化がONのとき、メインの色テクスチャ (_MainTex) を暗号化します。透明部分の縁を保つため、不透明度は変更しません。")]
        [SerializeField] private bool encryptMainTex = true;

        [Tooltip("テクスチャ暗号化がONのとき有効: 法線マップ (_NormalMap) を暗号化します。既定OFF。")]
        [SerializeField] private bool encryptNormalMap = false;

        [Tooltip("テクスチャ暗号化がONのとき有効: 2ndカラーレイヤー (_Main2ndTex) を暗号化します。")]
        [SerializeField] private bool encryptMain2nd = true;

        [Tooltip("テクスチャ暗号化がONのとき有効: 透明度マスク (_AlphaMask) を暗号化します。既定OFF。")]
        [SerializeField] private bool encryptAlphaMask = false;

        // v0.31.14 revert: encryptEmissionMap toggle は v0.31.13 で導入したが、
        // OVERRIDE_EMISSION_1ST inline 展開が複数 lilToon variant で compile error
        // (`fd.invLighting` / `fd.albedo` / `lilCalcBlink` 等が未定義) → 全 renderer 元 shader fallback
        // → 全 texture 露出という致死 regression を起こしたため、 spec entry / 専用 builder と一緒に撤去。
        // serialized field 自体も削除 (= v0.31.13 で保存された値は次の Save Project で消える、 機能無効のため無害)。

        [Tooltip("暗号化するテクスチャの最大解像度 (長辺、px)。これを超えるテクスチャは縮小してから暗号化し、AssetBundle容量の増加を抑えます。\n" +
                 "・既定2048: 1K/2Kはそのまま、4Kのみ2Kに縮小。Poiyomiの数値テクスチャは縮小しません。\n" +
                 "・容量目安 (従来の無圧縮の暗号化): 4K = 64 MB、2K = 16 MB、1K = 4 MB。\n" +
                 "・「圧縮したまま暗号化する」: DXT1・BC4相当は2K = 約2.7 MiB (4Kは約4倍、1Kは約1/4)。DXT5・BC5・BC7相当はその約2倍、RGBA 圧縮は約4倍。")]
        [Range(64, 8192)]
        [SerializeField] private int textureEncryptionMaxResolution = 2048;

        // 既定 ON。lilToon の全55用途と、Poiyomi の材質テクスチャ全般が対象 (暗号形式 3)。
        // Poiyomi の HDR・数値・非2D は OFF でも可逆形式で精度を保つ。形式は Editor の KcbCodecResolver が決定する。
        [Tooltip("テクスチャ暗号化がONのとき有効: lilToonの色・法線・マスク・発光の全55用途と、Poiyomiの色・法線・マスク・発光・MatCap・AOなどの材質テクスチャ全般を用途に合う形式で圧縮してから暗号化します。\n" +
                 "対応する画像の展開後の容量を減らします。画質と描画の負荷が変わります。Poiyomiと、法線・マスク・発光の追加対応の負荷は未測定です。異方性フィルタは適用されません。鍵の強さは変わりません。\n" +
                 "PC向けのビルドのみ。画像ごとに無圧縮へ戻せます。PoiyomiのHDR・数値・Cube・配列・3Dは、設定OFFや無圧縮指定でも精度を保つ可逆形式を使います。")]
        [SerializeField] private bool textureEncryptionKeepCompression = true;

        /// <summary>画像ごとの暗号化設定。Editor 側で画像の識別子を解決して使用する。</summary>
        [SerializeField] private TextureOverride[] textureOverrides = new TextureOverride[0];

        // ── v0.53: 「暗号化するテクスチャの種類」ウィンドウ (プロパティ単位選択) ──
        // 引き継ぎ前の挙動を変えないため、既定では false (= 従来対応していた用途に旧グループトグルを使う)。
        // Poiyomiの追加用途は未引き継ぎ・未記録とも既定OFF。対応カタログが増えても既存の対象を自動で増やさない。
        // ウィンドウで一度「適用」すると true になり、以後は texturePropertyStates を優先する。
        [SerializeField] private bool texturePropertySelectionMigrated = false;

        /// <summary>「暗号化するテクスチャの種類」ウィンドウで適用した、プロパティ単位の ON/OFF。</summary>
        [SerializeField] private TexturePropertyState[] texturePropertyStates = new TexturePropertyState[0];

        // ── v0.34.7: Fallback shader 用 placeholder (= VRChat Safety で shader fallback 中の他 user に低解像度 preview を見せる) ──
        // UI からは削除済み。過去のシーンデータを読み込むためフィールドだけ残し、値は参照しない。
        [Tooltip("VRChatのセーフティでシェーダーが無効化された相手には、低解像度のぼかし画像を表示します。正規に解錠している相手の見た目には影響しません。")]
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

        [Tooltip("各種のマスクテクスチャ (ブレンド・ディゾルブ・アウトライン幅・影・メタリック・滑らかさ・視差・ラメの形・ファーなど約29種) をまとめて暗号化します。既定OFF。")]
        [SerializeField] private bool encryptMaskGroup = false;

        [Tooltip("各種の色テクスチャ (2nd / 3rdレイヤー・アウトライン・MatCap・リム・影・逆光・反射・ラメ・グラデーションなど約13種) をまとめて暗号化します。既定ON。")]
        [SerializeField] private bool encryptColorGroup = true;

        [Tooltip("各種の法線マップ (2nd・異方性反射・MatCap用・アウトライン用・ファー用の6種) をまとめて暗号化します。既定OFF。")]
        [SerializeField] private bool encryptNormalGroup = false;

        [Tooltip("発光テクスチャ (発光のグラデーションを含む4種) をまとめて暗号化します。\n" +
                 "全lilToonバリエーションでの検証が終わるまでは、念のためOFFを推奨します。既定OFF。")]
        [SerializeField] private bool encryptEmissionGroup = false;

        // ── v0.34.15 (案 Y): lilToon カスタム派生 shader 互換 mode ──

        [Tooltip("lilToonをベースにした特殊シェーダー (公式lilToonそのものではなく改造・拡張版) を使うマテリアルを、保護の対象から完全に外します。\n" +
                 "例: BoundBoneProのlilToonSquish、lilToonの「カスタムシェーダー作成」で作ったもの、他ツールが作ったlilToon派生。\n" +
                 "これらは元のまま維持され、見た目も機能も保たれます。その代わりテクスチャは暗号化されず抜き取り可能になります。公式lilToonとPoiyomiは通常どおり暗号化されます。\n" +
                 "既定ON (特殊シェーダー使用時に真っ白になるのを防ぎます)。OFFにすると該当マテリアルが真っ白に表示されることがあります。")]
        [SerializeField] private bool skipCustomLilToonDerivatives = true;

        // ── Emergency disable switches (build-time、 group-level rollback) ──
        [Tooltip("緊急用: Maskグループの暗号化を強制的に無効化します (グループのトグルがONでも暗号化しません)。既定OFF。")]
        [SerializeField] private bool disableMaskGroup = false;

        [Tooltip("緊急用: Colorグループの暗号化を強制的に無効化します。既定OFF。")]
        [SerializeField] private bool disableColorGroup = false;

        [Tooltip("緊急用: Normalグループの暗号化を強制的に無効化します。既定OFF。")]
        [SerializeField] private bool disableNormalGroup = false;

        [Tooltip("緊急用: Emissionグループの暗号化を強制的に無効化します。既定OFF。")]
        [SerializeField] private bool disableEmissionGroup = false;

        [Tooltip("暗号化していないテクスチャへの参照を保護版マテリアルから取り除き、抜き取られないようにします。\n" +
                 "発光・MatCap・アウトライン・リム・2nd・影・ディテールなど見た目に重要なものは、品質維持のため残します。\n" +
                 "既定ON。OFFにすると全テクスチャ参照がマテリアルに残ります。")]
        [SerializeField] private bool stripUnencryptedTextureRefs = true;

        [Tooltip("VRCFuryがアバター実行時に生成するマテリアル・シェーダーには、ビルド時の暗号化を適用できず、テクスチャが抜き取られる可能性があります。\n" +
                 "既定OFF: VRCFuryを検出すると警告を表示します (ビルドは続行)。ONにするとその点を承知したものとして警告を出しません。")]
        [SerializeField] private bool acknowledgeVRCFuryLeak = false;

        [Tooltip("ビルド時にRendererのGameObject名をランダムな文字列に置き換えます。抜き取られたアバターで、どれが顔・髪・服かを分かりにくくします。\n" +
                 "アバターが正常に動作しなくなるのを避けるため、Humanoidボーンは対象外です。\n" +
                 "変更後はHierarchyやInspectorで対象を見つけにくくなります。")]
        [SerializeField] private bool enableGameObjectObfuscation = false;

        [Tooltip("ビルド時にBlendShapeの名前をランダムな文字列に置き換えます。順序を保ち、アニメーションや各種ツールからの参照も自動で更新するため、ギミックは引き続き動作します。\n" +
                 "抜き取り時に「Smile_L」「vrc.v_aa」等の意味のある名前が消えます。")]
        [SerializeField] private bool enableBlendShapeObfuscation = false;

        [Tooltip("BlendShape難読化がONのとき、MMDワールド用の標準モーフ (あ / い / う / まばたき等) を名前の置き換えから除外します (順序シャッフルには参加します)。\n" +
                 "MMDワールドはBlendShape名で表情を動かすため、除外しないと表情が動かなくなります。名前に「MMD」を含む区切り行も自動で除外されます。既定ON。")]
        [SerializeField] private bool excludeMmdBlendShapes = true;

        [Tooltip("ビルド時にアニメーターのレイヤー名・ステート名・パラメーター名をランダムな文字列に置き換え、参照も自動で同期します。\n" +
                 "VRChatが必須とするパラメーター (Viseme / Gesture / IsLocal等) と解錠用パラメーターは対象外なので、機能には影響しません。")]
        [SerializeField] private bool enableAnimatorObfuscation = false;

        [Tooltip("パラメーター難読化がONのとき、VRCOSC (心拍計・音声認識などの外部OSCアプリ) が使うパラメーター (名前が「VRCOSC/」で始まるもの) を置き換えから除外します。\n" +
                 "除外しないと心拍計などが機能停止します。既定ON。")]
        [SerializeField] private bool excludeVrcOscParameters = true;

        [Tooltip("ONにするとパラメーター難読化を再現可能にします。同じ元の名前は常に同じ難読名になり、PCとQuestを別々にビルドしても一致します。\n" +
                 "パラメーター難読化がONのときのみ効果があります。既定OFF (ビルドごとにランダム)。")]
        [SerializeField] private bool deterministicObfuscation = false;

        [Tooltip("同じ名前を再現するためのシード。32文字の16進数で、設定をONにすると自動生成します。\n" +
                 "PCとQuestを別々のPrefabで作る場合は、両方に同じ値を設定してください。")]
        [SerializeField] private string obfuscationSeedHex = "";

        [Tooltip("ビルド時に、アバターが参照するメッシュ・マテリアル・アニメーションなどのアセット名をランダムな文字列に置き換えます。抜き取り時に「Mesh_Body」「Smile_Anim」等の意味のある名前が消えます。\n" +
                 "アセットは複製してから変更するため、元のアセットには影響しません。保護処理に影響しないよう、シェーダーとテクスチャは対象外です。")]
        [SerializeField] private bool enableAssetNameObfuscation = false;

        [Tooltip("ビルド時に、各メッシュにダミーのBlendShapeをランダムな数 (32〜64個) 追加します。抜き取られたメッシュで、本物のViseme・表情のBlendShapeが意味不明なダミーに紛れて分かりにくくなります。\n" +
                 "本物の名前・順序・インデックスはすべて保つため、アニメーションや各種ツールからの参照には影響しません。\n" +
                 "副作用: メッシュのファイルサイズが少し増えます。")]
        [SerializeField] private bool enableBlendShapeDecoy = false;

        [Tooltip("追加するダミーBlendShapeの最小数。ビルドごとに、この値から最大数までの範囲でランダムに決めます。")]
        [Range(8, 128)]
        [SerializeField] private int blendShapeDecoyMinCount = 32;

        [Tooltip("追加するダミーBlendShapeの最大数。ビルドごとに、最小数からこの値までの範囲でランダムに決めます。")]
        [Range(8, 128)]
        [SerializeField] private int blendShapeDecoyMaxCount = 64;

        // ────────────────────────────── Decoy Animator (v0.28) ──────────────────────────────

        [Tooltip("解析を惑わすため、ダミーのSkinnedMeshRenderer・マテリアル・アニメーターをアバターに追加します。本物の解錠処理とダミーが見分けにくくなります。\n" +
                 "ダミーは同期パラメーターを消費しません。\n" +
                 "副作用: Hierarchyやアセット一覧にダミーが多数並び、Performance Rankに影響する場合があります。")]
        [SerializeField] private bool enableDecoyAnimator = false;

        [Tooltip("追加するダミーSkinnedMeshRendererの最小数。ビルドごとに、この値から最大数までの範囲でランダムに決めます。")]
        [Range(0, 16)]
        [SerializeField] private int decoyRendererMinCount = 2;

        [Tooltip("追加するダミーSkinnedMeshRendererの最大数。ビルドごとに、最小数からこの値までの範囲でランダムに決めます。")]
        [Range(0, 16)]
        [SerializeField] private int decoyRendererMaxCount = 5;

        [Tooltip("ダミーSkinnedMeshRendererごとに追加するダミーBlendShapeの数。すべて同じ数を追加します。")]
        [Range(1, 64)]
        [SerializeField] private int decoyBlendShapePerRenderer = 8;

        [Tooltip("ダミーパラメーターの依存関係の最小段数。2段なら、パラメーターA → Layer1 → パラメーターB → Layer2 → BlendShapeの順につなぎます。")]
        [Range(1, 6)]
        [SerializeField] private int decoyParameterChainMin = 2;

        [Tooltip("ダミーパラメーターの依存関係の最大段数。ビルドごとに、最小段数からこの値までの範囲でランダムに決めます。")]
        [Range(1, 6)]
        [SerializeField] private int decoyParameterChainMax = 3;

        [Tooltip("依存関係の各段で生成するダミーパラメーターの数。同じ段のダミーレイヤー数と一致します。")]
        [Range(1, 16)]
        [SerializeField] private int decoyParametersPerChain = 4;

        // ────────────────────────────── Hierarchy Shuffle (v0.29) ──────────────────────────────

        [Tooltip("アバター配下のオブジェクトの並び順をランダムに入れ替えます。抜き取ったアバターの構造を、並び順から推測しにくくします。\n" +
                 "腕・脚のボーンやPhysBoneなど並び順に依存する部分は並び替えません。VRCFuryを使っているアバターでは並び替えません。\n" +
                 "注意: Modular Avatarのメニュー順は並び替えで乱れることがあります。下の設定 (既定ON) で防げます。")]
        [SerializeField] private bool enableHierarchyShuffle = false;

        [Tooltip("Hierarchy並び替えがONのとき、Modular Avatarのメニュー項目を持つ親の並び替えを除外し、メニューの順序を保ちます。\n" +
                 "MAはオブジェクトの並び順でメニュー項目順を決めるため、除外しないとメニューが乱れます。既定ON。")]
        [SerializeField] private bool preserveMaMenuOrder = true;

        // ────────────────────────────── 対象別絞り込み (v0.42) ──────────────────────────────
        // #2/#3/#5: 難読化の「対象」をユーザーが個別に絞り込む (= blacklist 除外) 統合設定。
        // 既存の excludeFromShaderLock / excludeFromTextureEncryption / MmdBlendShapeWhitelist と
        // 同じ「全対象にかけて個別に外す」設計に揃える。

        [Tooltip("GameObject名の難読化から除外するGameObjectを指定します。指定したGameObjectは元の名前のまま維持されます。\n" +
                 "用途: 名前やパスで参照される外部連携・OSC・デバッグ対象など。\n" +
                 "GameObject名難読化がONのときのみ効果があります。")]
        [SerializeField] private GameObject[] obfuscationExcludeGameObjects = new GameObject[0];

        [Tooltip("v0.42以降の設定です。ONにすると、上で指定したGameObjectの子・孫などもすべて名前の難読化から除外します。\n" +
                 "OFFの場合は、指定したGameObject自身だけを除外します。\n" +
                 "衣装のルートを1つ指定すれば配下のメッシュをまとめて除外できるよう、初期設定はONです。")]
        [SerializeField] private bool obfuscationExcludeIncludeChildren = true;

        [Tooltip("BlendShape難読化がONのとき、フェイストラッキング (VRCFaceTracking / ARKit等) の標準BlendShape名を名前の置き換えから除外します (順序シャッフルには参加します)。\n" +
                 "フェイストラッキングはBlendShape名で駆動するため、除外しないと連携が切れます。既定ON。")]
        [SerializeField] private bool excludeFaceTrackingBlendShapes = true;

        [Tooltip("BlendShape難読化から手動で除外するBlendShape名 (大文字小文字を区別しない完全一致)。\n" +
                 "フェイストラッキングのプリセットで検出できない、作者独自の名前を指定するときに使います。指定したBlendShapeは元の名前のまま残ります。")]
        [SerializeField] private string[] extraBlendShapesToExclude = new string[0];

        [Tooltip("フェイストラッキング用のパラメーター (名前が「v2/」で始まる等) をパラメーター難読化から除外します。\n" +
                 "VRCFaceTracking等は名前でOSC送信するため、除外しないと顔トラッキングが機能停止します。既定ON。")]
        [SerializeField] private bool excludeFaceTrackingParameters = true;

        [Tooltip("パラメーター難読化から手動で除外するパラメーター名。\n" +
                 "なでなで等の接触ギミックのパラメーターは自動で除外します。検出できなかった場合はここで指定してください。\n" +
                 "完全一致のほか、末尾を「/」にすると前方一致になります (例: 'MyGimmick/')。指定したパラメーターは元の名前のまま残ります。")]
        [SerializeField] private string[] excludeParameterNamesFromObfuscation = new string[0];

        // ────────────────────────────── 詳細 ──────────────────────────────

        [Tooltip("ビルド時にConsoleへ詳細ログを出すか")]
        [SerializeField] private bool verboseLogging = false;

        // v0.37.8 導入 / v0.38: default ON。 生成 shader を build 間で保持して Unity ShaderCache を有効化。
        // OFF だと build 終了時に Generated/Shaders/_AR_*.shader が全削除され、 次 build で Unity は
        // 新 shader 扱いで全 variant を 1 から compile し直す (= 多 material avatar で数十分)。
        // ON で sweep を skip し、 content-skip (WriteIfChanged) と組み合わせて同一内容なら write/import
        // を省略 → cache hit で compile を数分単位に短縮する。 旧ファイルは GeneratedShaderSweepPass が
        // version sentinel ベースで掃除する (同 version は全保持)。
        // trade-off: locked shader file が disk に残るため attacker が shader 構造を解析可能になる
        // (ただし texture 復号には実行時の _AR_TK (共有時8byte、独立鍵時4byte) が必要で、shader 抽出だけでは
        // texture 復号は不可)。 解析耐性を最大化したい配布時は OFF にできる (任意)。
        [Tooltip("生成したシェーダーをビルド後も保持し、2回目以降のビルドを高速化します。初期設定はONです。マテリアルが多いアバターでは、数十分かかるビルドが数分に短縮されます。\n" +
                 "保護シェーダーがプロジェクト内に残るため、解析者がシェーダーの構造を見る可能性があります。テクスチャの復号には別途解錠が必要で、シェーダーだけでは復号できません。\n" +
                 "解析への耐性をできるだけ高めたい配布時は、OFFにできます。")]
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
        public bool MeshKeyEncryption => enableMeshLock && meshKeyEncryption;
        public bool UseSharedEncryptionKey => MeshKeyEncryption && EnableTexturePixelEncryption && shareMeshAndTextureKey;
        public string MeshDrawingKeyHex => meshDrawingKeyHex ?? "";
        public bool HasMeshDrawingKey
        {
            get
            {
                var hex = MeshDrawingKeyHex;
                if (hex.Length != KeyByteCount * 2) return false;
                bool nonZero = false;
                foreach (char c in hex)
                {
                    bool digit = c >= '0' && c <= '9';
                    bool lower = c >= 'a' && c <= 'f';
                    bool upper = c >= 'A' && c <= 'F';
                    if (!digit && !lower && !upper) return false;
                    nonZero |= c != '0';
                }
                return nonZero;
            }
        }
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

        // Preserve the serialized legacy choice independently of automatic texture support.
        public bool LegacyShaderLevelDecodeEnabled => enableShaderLevelDecode;

        public static bool ComputeEnableShaderLevelDecode(bool rawLegacyShaderLevelDecode, bool rawEnableTexturePixelEncryption)
            => rawLegacyShaderLevelDecode || rawEnableTexturePixelEncryption;

        public bool EnableShaderLevelDecode =>
            ComputeEnableShaderLevelDecode(enableShaderLevelDecode, enableTexturePixelEncryption);

        // v0.37+: MR→SMR 変換 toggle (= lockable shader を持つ MR を build 時に SMR 化)。
        // 従来シェーダー保護とメッシュ暗号化のどちらからも利用する。
        public bool EnableMeshRendererToSkinnedConversion =>
            enableMeshRendererToSkinnedConversion && (enableShaderLevelDecode || MeshKeyEncryption);

        public string[] ExtraShaderNamesToLock => extraShaderNamesToLock ?? new string[0];
        public string[] ExcludeFromShaderLock => excludeFromShaderLock ?? new string[0];

        // ── Texture Pixel Encryption (v0.31) ──
        // master が OFF のとき子 toggle は全て無効化される。
        // 必要な shader hook は EnableShaderLevelDecode が自動で有効にする。
        /// <summary>
        /// テクスチャ暗号化の選択値を返す。第2引数は旧APIとの互換のため残す。
        /// シェーダー復号の旧設定がOFFでも、テクスチャ暗号化に必要な処理は自動で有効になる。
        /// </summary>
        public static bool ComputeEnableTexturePixelEncryption(bool rawEnableTexturePixelEncryption, bool rawEnableShaderLevelDecode)
            => rawEnableTexturePixelEncryption;

        public bool EnableTexturePixelEncryption =>
            ComputeEnableTexturePixelEncryption(enableTexturePixelEncryption, enableShaderLevelDecode);
        /// <summary>diagnostic: テクスチャ暗号化の保存済み選択値。</summary>
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

        // v0.53: 緊急無効化スイッチの生値 (IsPropertyEnabled の一元判定が、引き継ぎ後も master/緊急停止だけは
        // 効き続けるようにするため個別に公開する)。 EncryptXGroup とは異なり EnableTexturePixelEncryption との
        // AND は取らない (呼び出し側で 1 の判定と組み合わせて使う契約)。
        public bool DisableColorGroup => disableColorGroup;
        public bool DisableNormalGroup => disableNormalGroup;
        public bool DisableMaskGroup => disableMaskGroup;
        public bool DisableEmissionGroup => disableEmissionGroup;

        // ── v0.53: 「暗号化するテクスチャの種類」ウィンドウ ──
        public bool TexturePropertySelectionMigrated => texturePropertySelectionMigrated;

        /// <summary>プロパティ単位の ON/OFF 記録。未設定時は空配列を返す。</summary>
        public TexturePropertyState[] TexturePropertyStates => texturePropertyStates ?? new TexturePropertyState[0];

        /// <summary>(family, property) の記録を探す。見つかれば true を返し、値を enabled に入れる。</summary>
        public bool TryGetTexturePropertyState(string family, string property, out bool enabled)
        {
            enabled = false;
            if (texturePropertyStates == null || string.IsNullOrEmpty(family) || string.IsNullOrEmpty(property))
                return false;
            for (int i = 0; i < texturePropertyStates.Length; i++)
            {
                var state = texturePropertyStates[i];
                if (state == null) continue;
                if (string.Equals(state.family, family, System.StringComparison.Ordinal)
                    && string.Equals(state.property, property, System.StringComparison.Ordinal))
                {
                    enabled = state.enabled;
                    return true;
                }
            }
            return false;
        }

        // v0.34.15: lilToon カスタム派生 shader 互換 mode accessor (BBP / 「カスタムシェーダー作成」 派生 / サードパーティ vendor 派生 等)
        public bool SkipCustomLilToonDerivatives => skipCustomLilToonDerivatives;

        // v0.31.14 revert: EncryptEmissionMap property は v0.31.13 で追加したが致死 regression のため撤去。
        // v0.31.12: locked variant material から暗号化対象外 texture 参照を剥がす (= leak 防止、 visual fidelity 損失あり)
        // EnableTexturePixelEncryption が ON でない時は意味がない (= 暗号化 texture 参照自体が無い) ので AND ゲート。
        public bool StripUnencryptedTextureRefs => EnableTexturePixelEncryption && stripUnencryptedTextureRefs;
        public bool AcknowledgeVRCFuryLeak => acknowledgeVRCFuryLeak;
        public int TextureEncryptionMaxResolution => SnapTextureResolution(textureEncryptionMaxResolution);

        /// <summary>
        /// 圧縮を保ったまま暗号化する設定。既定 ON。テクスチャ暗号化そのものが OFF のときの扱いは
        /// 呼び出し側 (暗号化の計画) が決める。この値は設定の値だけを返す。
        /// </summary>
        public bool TextureEncryptionKeepCompression => textureEncryptionKeepCompression;

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

        /// <summary>表示状態の入力。メッシュ暗号化はローカルInvBroadcastの0/1ヒント、従来方式は同期Broadcastを使う。</summary>
        public string GetDisplayStateParamName() => MeshKeyEncryption ? GetInvBroadcastParamName() : GetBroadcastParamName();

        public string GetUnlockBlendShapeName() => HasMeshLockUnlockBlendShapeName ? meshLockUnlockBlendShapeName : "_AjisaiAR_Unlock";

        public string GetHolderObjectName() => HasMeshLockHolderObjectName ? meshLockHolderObjectName : "_AjisaiAR_Lock";

        public string GetScoreParamName() => string.IsNullOrEmpty(meshLockScoreParamName) ? "_AjisaiAR_Score" : meshLockScoreParamName;
        public string GetOneParamName() => string.IsNullOrEmpty(meshLockOneParamName) ? "_AjisaiAR_One" : meshLockOneParamName;
        public string GetInvBroadcastParamName() =>
            string.IsNullOrEmpty(meshLockInvBroadcastParamName) ? "_AjisaiAR_InvBroadcast" : meshLockInvBroadcastParamName;

        // メッシュ暗号化では分割同期Intの2ch目、従来方式では再送用Float。旧名は維持する。
        public string GetHeartbeatParamName() =>
            string.IsNullOrEmpty(meshLockHeartbeatParamName) ? "_AjisaiAR_Heartbeat" : meshLockHeartbeatParamName;

        public string[] MeshLockTextureKeyParamNames => meshLockTextureKeyParamNames ?? new string[0];

        public bool HasMeshLockTextureKeyParamNames =>
            meshLockTextureKeyParamNames != null
            && meshLockTextureKeyParamNames.Length == TextureKeyByteCount
            && System.Array.TrueForAll(meshLockTextureKeyParamNames, n => !string.IsNullOrEmpty(n));

        // テクスチャ鍵TK_iのOSC名。メッシュ暗号化ではlocal Int、従来方式ではsynced Int。
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

        public Texture[] ExcludeTexturesFromEncryption => excludeTexturesFromEncryption ?? new Texture[0];

        /// <summary>
        /// v0.51: src texture が ExcludeTexturesFromEncryption list に含まれているか判定 (テクスチャ単位除外)。
        /// 含まれていれば texture pixel encryption を skip する (= 平文で AssetBundle に焼かれ leak 許容、 shader-lock /
        /// mesh 保護は不変)。 元 (src) texture asset の reference 比較で、 prefab material に貼られている original texture
        /// のみマッチする (= clone / 暗号化済 variant は別 reference)。 引数は Texture 型で受け、 material.GetTexture の
        /// 戻り値をそのまま渡せるようにする。Cube・配列・3D の参照も同じように照合する。
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

        /// <summary>Drawing/shared master key, never derived from the legacy texture key.</summary>
        public byte[] GetMeshDrawingKeyBytes()
        {
            var bytes = new byte[KeyByteCount];
            if (!HasMeshDrawingKey) return bytes;
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(meshDrawingKeyHex.Substring(i * 2, 2),
                    System.Globalization.NumberStyles.HexNumber);
            return bytes;
        }
    }
}
