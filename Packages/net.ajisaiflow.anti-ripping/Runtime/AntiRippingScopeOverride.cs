using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace AjisaiFlow.AntiRipping
{
    /// <summary>
    /// v0.42+: 特定の GameObject に付けて、 その GO (+子) に紐づく難読化を「次元ごと」に除外する
    /// per-GameObject スコープ override コンポーネント。 アバタールートの <see cref="AntiRippingTag"/> が
    /// 全体方針を持つのに対し、 こちらは「この衣装/このメッシュだけ難読化から外す」局所的な上書き。
    ///
    /// 対象次元は GO に「綺麗に紐づく」 5 つに限定する (param / asset 名 / AnimatorController は
    /// avatar 全体のグローバル名前空間で per-GO 所有が成立しないため対象外。 それらは AntiRippingTag の
    /// グローバル除外リストで扱う):
    ///   - Hierarchy 並び替え: この GO の子順序を保持する (この GO 配下を撹乱しない)
    ///   - BlendShape 難読化: この GO の SkinnedMeshRenderer の sharedMesh の BlendShape を rename しない
    ///   - Mesh Lock (頂点ロック): この GO の SkinnedMeshRenderer を Mesh Lock の頂点スクランブルから除外する
    ///     (SPS plug 等、 シェーダーで rest-pose 頂点を変形する mesh は頂点配列の置換で変形が破綻するため)
    ///   - テクスチャ暗号化: この GO の Renderer が参照する material の texture を暗号化しない
    ///   - シェーダーロック: この GO の Renderer を material の locked variant 差し替えから除外する
    ///     (SPS/TPS 等、 ビルド時に material shader を patch する外部ツールとの併用向け。 v0.48)
    ///
    /// INDMFEditorOnly を実装しているため、 ビルド成果物には残らない (AntiRippingTag と同様)。
    /// 各 NDMF パスはビルド時に下記の静的コレクタで除外集合を構築して尊重する。
    ///
    /// 注意 (共有リソース): texture/material と mesh は複数 GO で共有され得る。 共有 material/mesh を
    /// 1 つの GO で除外すると、 同じ material/mesh を使う他 GO でも難読化されない (除外が広く波及する)。
    /// Hierarchy 並び替えは共有リソース (material / mesh) を持たないため、 この共有問題は発生しない。
    /// </summary>
    [AddComponentMenu("紫陽花広場/VRChat Anti-Ripping Scope Override (per-GameObject)")]
    [DisallowMultipleComponent]
    public sealed class AntiRippingScopeOverride : MonoBehaviour, INDMFEditorOnly
    {
        [Tooltip("ONにすると、このGameObjectの子オブジェクトをHierarchyの並び替えから除外し、元の順序を保ちます。\n" +
                 "メニュー項目や衣装内のオブジェクトなど、並び順を固定したい場合に使います。")]
        [SerializeField] private bool excludeHierarchyShuffle = false;

        [Tooltip("ONにすると、このGameObjectのSkinnedMeshRendererが参照するメッシュのBlendShapeを\n" +
                 "名前の難読化から除外します。BlendShape名を指定して動かす外部ツールと連携する場合に使います。\n" +
                 "同じメッシュを他のGameObjectも使っている場合、そのメッシュ全体が除外されます。")]
        [SerializeField] private bool excludeBlendShapeObfuscation = false;

        [Tooltip("ONにすると、このGameObjectのSkinnedMeshRenderer / MeshRendererをメッシュロックの頂点散らしと試験対応のメッシュ暗号化から除外します。\n" +
                 "SPS plugなど、シェーダーで初期状態の頂点を変形させるメッシュは、メッシュロックで頂点を散らすと正常に変形しなくなります。\n" +
                 "除外すると元のメッシュを保つため、SPS等が正常に動作します。\n" +
                 "除外したメッシュはメッシュロックで保護されず、抜き取りへの耐性が下がります。同じメッシュを他のGameObjectも使っている場合、そのメッシュ全体が除外されます。")]
        [SerializeField] private bool excludeMeshLockVertexScramble = false;

        [Tooltip("ONにすると、このGameObjectのRendererが参照するマテリアルのテクスチャを\n" +
                 "暗号化から除外します。元の画像がAssetBundleに残り、抜き取れる状態になります。\n" +
                 "同じマテリアルを他のGameObjectも使っている場合、そのマテリアル全体が除外されます。")]
        [SerializeField] private bool excludeTextureEncryption = false;

        [Tooltip("ONにすると、このGameObjectのRenderer (SkinnedMeshRenderer / MeshRenderer) を、保護版シェーダーへの差し替えから除外します。\n" +
                 "マテリアルは元のシェーダーを使い続け、\n" +
                 "このRendererのマテリアルにはテクスチャ暗号化も適用しません。\n" +
                 "SPS plugなど、ビルド時にシェーダーを書き換える外部ツールと併用する場合に使います。\n" +
                 "除外したRendererのマテリアル・テクスチャは保護されません。他のGameObjectとマテリアルを共有している場合、\n" +
                 "共有先のテクスチャも暗号化せずに同梱されるため、共有先で暗号化しても保護できません。")]
        [SerializeField] private bool excludeShaderLockSwap = false;

        [Tooltip("ONにすると、上記の除外をこのGameObjectの子・孫などにも適用します。初期設定はONです。\n" +
                 "OFFの場合は、このGameObject自身だけが対象です。")]
        [SerializeField] private bool includeChildren = true;

        public bool ExcludeHierarchyShuffle => excludeHierarchyShuffle;
        public bool ExcludeBlendShapeObfuscation => excludeBlendShapeObfuscation;
        public bool ExcludeMeshLockVertexScramble => excludeMeshLockVertexScramble;
        public bool ExcludeTextureEncryption => excludeTextureEncryption;
        public bool ExcludeShaderLockSwap => excludeShaderLockSwap;
        public bool IncludeChildren => includeChildren;

        /// <summary>いずれかの次元の除外が有効か (UI 表示・skip 判定用)。</summary>
        public bool HasAnyExclusion =>
            excludeHierarchyShuffle || excludeBlendShapeObfuscation
            || excludeMeshLockVertexScramble || excludeTextureEncryption
            || excludeShaderLockSwap;

        // ────────────────────────────── 静的コレクタ (各 NDMF パスがビルド時に呼ぶ) ──────────────────────────────

        /// <summary>
        /// Hierarchy 並び替えから除外する「親 Transform」集合を集める。
        /// excludeHierarchyShuffle が ON の GO 自身 (+ IncludeChildren なら子孫) を入れる。
        /// HierarchyShufflePass はこの集合に含まれる親の子順序をシャッフルしない。
        /// </summary>
        public static HashSet<Transform> CollectShuffleExcludedTransforms(GameObject avatar)
            => CollectExcluded<Transform, Transform>(avatar, ov => ov.excludeHierarchyShuffle, t => new[] { t });

        /// <summary>
        /// 4 つの Collect* (Shuffle/Mesh/Texture/ShaderLock) に共通する形を集約したヘルパー。
        /// 「excludeXxx が ON の override を走査 → IncludeChildren に応じて対象 Component を集める
        /// (ON なら GetComponentsInChildren、OFF なら自 GO の GetComponents のみ) → selector で
        /// 除外対象の値へ変換して集合化」という手順は 4 メソッドで同一。 isExcluded / selector だけが異なる。
        /// </summary>
        private static HashSet<TResult> CollectExcluded<TComponent, TResult>(
            GameObject avatar,
            Func<AntiRippingScopeOverride, bool> isExcluded,
            Func<TComponent, TResult[]> selector)
            where TComponent : Component
            where TResult : UnityEngine.Object
        {
            var set = new HashSet<TResult>();
            if (avatar == null) return set;
            foreach (var ov in avatar.GetComponentsInChildren<AntiRippingScopeOverride>(true))
            {
                if (ov == null || !isExcluded(ov)) continue;
                var components = ov.includeChildren
                    ? ov.GetComponentsInChildren<TComponent>(true)
                    : ov.GetComponents<TComponent>();
                for (int i = 0; i < components.Length; i++)
                {
                    if (components[i] == null) continue;
                    var results = selector(components[i]);
                    if (results == null) continue;
                    for (int j = 0; j < results.Length; j++)
                    {
                        if (results[j] != null) set.Add(results[j]);
                    }
                }
            }
            return set;
        }

        /// <summary>
        /// BlendShape 難読化から除外する Mesh 集合を集める。
        /// excludeBlendShapeObfuscation が ON の GO (+ IncludeChildren なら子孫) の SkinnedMeshRenderer の sharedMesh。
        /// 共有 mesh はいずれかの GO で除外されると全体が除外される (mesh 単位)。
        /// </summary>
        public static HashSet<Mesh> CollectBlendShapeExcludedMeshes(GameObject avatar)
            => CollectMeshExcludedMeshes(avatar, false);

        /// <summary>BlendShape / Mesh Lock の共通条件で除外対象 Mesh を集める。</summary>
        private static HashSet<Mesh> CollectMeshExcludedMeshes(GameObject avatar, bool meshLock)
            => CollectExcluded<SkinnedMeshRenderer, Mesh>(avatar,
                ov => meshLock ? ov.excludeMeshLockVertexScramble : ov.excludeBlendShapeObfuscation,
                smr => smr.sharedMesh != null ? new[] { smr.sharedMesh } : Array.Empty<Mesh>());

        /// <summary>
        /// Mesh Lock (頂点スクランブル + Unlock BlendShape) から除外する Mesh 集合を集める。
        /// excludeMeshLockVertexScramble が ON の GO (+ IncludeChildren なら子孫) の SMR / MR の sharedMesh。
        /// MeshLockPass はこの集合に含まれる sharedMesh を持つ SMR を完全に skip する (頂点 collapse / Unlock BlendShape 追加 /
        /// 描画停止 (m_Enabled=0) / 解錠対象登録 のいずれも行わない)。 SPS plug 等、 シェーダーで rest-pose 頂点を変形する
        /// mesh は頂点配列を置換されると変形が破綻するため、 これで元 mesh を保持して保護対象外にする。
        /// 共有 mesh はいずれかの GO で除外されると全体が除外される (mesh 単位、 BlendShape 難読化除外と同一意味論)。
        /// </summary>
        public static HashSet<Mesh> CollectMeshLockExcludedMeshes(GameObject avatar)
        {
            var meshes = CollectMeshExcludedMeshes(avatar, true);
            meshes.UnionWith(CollectExcluded<MeshRenderer, Mesh>(avatar, ov => ov.excludeMeshLockVertexScramble,
                renderer =>
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    return filter != null && filter.sharedMesh != null ? new[] { filter.sharedMesh } : Array.Empty<Mesh>();
                }));
            return meshes;
        }

        /// <summary>
        /// テクスチャ暗号化から除外する Material 集合を集める (元 prefab/scene の sharedMaterials reference)。
        /// excludeTextureEncryption が ON の GO (+ IncludeChildren なら子孫) の Renderer.sharedMaterials。
        /// 既存の AntiRippingTag.ExcludeFromTextureEncryption と同じ「元 material reference 比較」で機能する。
        /// 共有 material はいずれかの GO で除外されると全体が除外される (material 単位)。
        /// </summary>
        public static HashSet<Material> CollectTextureExcludedMaterials(GameObject avatar)
            => CollectExcluded<Renderer, Material>(avatar, ov => ov.excludeTextureEncryption,
                r => r.sharedMaterials ?? Array.Empty<Material>());

        /// <summary>
        /// シェーダーロック (material の locked variant swap) + テクスチャ暗号化から除外する Renderer 集合を集める。
        /// excludeShaderLockSwap が ON の GO (+ IncludeChildren なら子孫) の Renderer (SMR + MR)。
        /// 他次元 (mesh/material 単位) と異なり Renderer 単位 (= material swap の適用単位) で除外する。
        /// ShaderLockPass / TextureEncryptionPass は SPS plug の自動検出 (PenetrationSystemDetector) との OR で
        /// この集合を尊重する。 手動除外には共有 material ガードを適用しない (ユーザーの明示意思を尊重) が、
        /// 共有を検出した場合はビルドログに Warn が出る。
        /// </summary>
        public static HashSet<Renderer> CollectShaderLockExcludedRenderers(GameObject avatar)
            => CollectExcluded<Renderer, Renderer>(avatar, ov => ov.excludeShaderLockSwap, r => new[] { r });
    }
}
