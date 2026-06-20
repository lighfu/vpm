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
    /// 対象次元は GO に「綺麗に紐づく」 3 つに限定する (param / asset 名 / AnimatorController は
    /// avatar 全体のグローバル名前空間で per-GO 所有が成立しないため対象外。 それらは AntiRippingTag の
    /// グローバル除外リストで扱う):
    ///   - Hierarchy 並び替え: この GO の子順序を保持する (この GO 配下を撹乱しない)
    ///   - BlendShape 難読化: この GO の SkinnedMeshRenderer の sharedMesh の BlendShape を rename しない
    ///   - テクスチャ暗号化: この GO の Renderer が参照する material の texture を暗号化しない
    ///
    /// INDMFEditorOnly を実装しているため、 ビルド成果物には残らない (AntiRippingTag と同様)。
    /// 各 NDMF パスはビルド時に下記の静的コレクタで除外集合を構築して尊重する。
    ///
    /// 注意 (共有リソース): texture/material と mesh は複数 GO で共有され得る。 共有 material/mesh を
    /// 1 つの GO で除外すると、 同じ material/mesh を使う他 GO でも難読化されない (除外が広く波及する)。
    /// Hierarchy 並び替えは sibling 順序が副作用ゼロのため、 この共有問題は発生しない。
    /// </summary>
    [AddComponentMenu("紫陽花広場/VRChat Anti-Ripping Scope Override (per-GameObject)")]
    [DisallowMultipleComponent]
    public sealed class AntiRippingScopeOverride : MonoBehaviour, INDMFEditorOnly
    {
        [Tooltip("ON のとき、 この GameObject の子順序を Hierarchy 並び替え (撹乱) から除外し、 並びを保持する。\n" +
                 "メニュー以外でも『この衣装の中身の並びを固定したい』等に使う。")]
        [SerializeField] private bool excludeHierarchyShuffle = false;

        [Tooltip("ON のとき、 この GameObject の SkinnedMeshRenderer の sharedMesh の BlendShape を\n" +
                 "難読化 (rename) から除外する。 名前で BlendShape を駆動する外部連携を維持したいメッシュに使う。\n" +
                 "注意: 同じ mesh を他 GO も使っている場合、 その mesh 全体が除外される (共有)。")]
        [SerializeField] private bool excludeBlendShapeObfuscation = false;

        [Tooltip("ON のとき、 この GameObject の Renderer が参照する material の texture を\n" +
                 "テクスチャ暗号化から除外する (= 元 texture が AssetBundle に残り抽出可能になる)。\n" +
                 "注意: 同じ material を他 GO も使っている場合、 その material 全体が除外される (共有)。")]
        [SerializeField] private bool excludeTextureEncryption = false;

        [Tooltip("ON (既定) のとき、 上記の除外をこの GameObject の子孫にも適用する。\n" +
                 "OFF のときはこの GameObject 自身のみが対象。")]
        [SerializeField] private bool includeChildren = true;

        public bool ExcludeHierarchyShuffle => excludeHierarchyShuffle;
        public bool ExcludeBlendShapeObfuscation => excludeBlendShapeObfuscation;
        public bool ExcludeTextureEncryption => excludeTextureEncryption;
        public bool IncludeChildren => includeChildren;

        /// <summary>いずれかの次元の除外が有効か (UI 表示・skip 判定用)。</summary>
        public bool HasAnyExclusion =>
            excludeHierarchyShuffle || excludeBlendShapeObfuscation || excludeTextureEncryption;

        // ────────────────────────────── 静的コレクタ (各 NDMF パスがビルド時に呼ぶ) ──────────────────────────────

        /// <summary>
        /// Hierarchy 並び替えから除外する「親 Transform」集合を集める。
        /// excludeHierarchyShuffle が ON の GO 自身 (+ IncludeChildren なら子孫) を入れる。
        /// HierarchyShufflePass はこの集合に含まれる親の子順序をシャッフルしない。
        /// </summary>
        public static HashSet<Transform> CollectShuffleExcludedTransforms(GameObject avatar)
        {
            var set = new HashSet<Transform>();
            if (avatar == null) return set;
            foreach (var ov in avatar.GetComponentsInChildren<AntiRippingScopeOverride>(true))
            {
                if (ov == null || !ov.excludeHierarchyShuffle) continue;
                set.Add(ov.transform);
                if (ov.includeChildren)
                {
                    var children = ov.GetComponentsInChildren<Transform>(true);
                    for (int i = 0; i < children.Length; i++) set.Add(children[i]);
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
        {
            var set = new HashSet<Mesh>();
            if (avatar == null) return set;
            foreach (var ov in avatar.GetComponentsInChildren<AntiRippingScopeOverride>(true))
            {
                if (ov == null || !ov.excludeBlendShapeObfuscation) continue;
                var smrs = ov.includeChildren
                    ? ov.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    : ov.GetComponents<SkinnedMeshRenderer>();
                for (int i = 0; i < smrs.Length; i++)
                {
                    if (smrs[i] != null && smrs[i].sharedMesh != null) set.Add(smrs[i].sharedMesh);
                }
            }
            return set;
        }

        /// <summary>
        /// テクスチャ暗号化から除外する Material 集合を集める (元 prefab/scene の sharedMaterials reference)。
        /// excludeTextureEncryption が ON の GO (+ IncludeChildren なら子孫) の Renderer.sharedMaterials。
        /// 既存の AntiRippingTag.ExcludeFromTextureEncryption と同じ「元 material reference 比較」で機能する。
        /// 共有 material はいずれかの GO で除外されると全体が除外される (material 単位)。
        /// </summary>
        public static HashSet<Material> CollectTextureExcludedMaterials(GameObject avatar)
        {
            var set = new HashSet<Material>();
            if (avatar == null) return set;
            foreach (var ov in avatar.GetComponentsInChildren<AntiRippingScopeOverride>(true))
            {
                if (ov == null || !ov.excludeTextureEncryption) continue;
                var renderers = ov.includeChildren
                    ? ov.GetComponentsInChildren<Renderer>(true)
                    : ov.GetComponents<Renderer>();
                for (int i = 0; i < renderers.Length; i++)
                {
                    var mats = renderers[i] != null ? renderers[i].sharedMaterials : null;
                    if (mats == null) continue;
                    for (int m = 0; m < mats.Length; m++)
                    {
                        if (mats[m] != null) set.Add(mats[m]);
                    }
                }
            }
            return set;
        }
    }
}
