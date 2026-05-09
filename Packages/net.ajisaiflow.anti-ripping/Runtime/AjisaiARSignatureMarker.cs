using nadena.dev.ndmf;
using UnityEngine;

namespace AjisaiFlow.AntiRipping
{
    /// <summary>
    /// HierarchyWatermarkPass が貼る forensic 用 MonoBehaviour。
    /// 抽出 prefab で MonoBehaviour 経由で signature が grep 可能になる…旨で v0.34.7 まで運用したが、
    /// 新 VRChat SDK (`master-build-2026-05-08-uncle-bibbleaxe` 系) が未知 MonoBehaviour を hard fail
    /// 扱いするようになり、 「validation issue: will be removed by the client」が build 失敗を引き起こすため、
    /// v0.34.8 から <see cref="INDMFEditorOnly"/> を実装して NDMF EditorOnly removal で剥がす。
    ///
    /// 残る forensic 経路:
    ///   - GameObject 名 (= `{baseName} | {signature}`) は asset bundle に残る (= prefab 抽出で grep 可能)
    ///   - Material の override tag `AjisaiAR_Signature` (AssetWatermarkPass の SetOverrideTag 経路) は残る
    /// 失われる経路:
    ///   - 本 MonoBehaviour 経由の rich data (creator/license/contactInfo/buildId/buildTimestampUtc) は剥がれる
    ///
    /// AAO empty-GO strip 回避は依然有効: NDMF EditorOnly removal は AAO Optimizing の後に走るため、
    /// AAO は本 component が貼られた状態で GameObject を見て strip skip する。
    ///
    /// runtime callback は持たない (= VRChat avatar runtime cost = 0)。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")] // Inspector の Add Component 一覧に出さない (= ユーザが誤って手動追加するのを防ぐ)
    public sealed class AjisaiARSignatureMarker : MonoBehaviour, INDMFEditorOnly
    {
        [SerializeField] internal string signature = "";
        [SerializeField] internal string creator = "";
        [SerializeField] internal string license = "";
        [SerializeField] internal string contactInfo = "";
        [SerializeField] internal string buildId = "";
        [SerializeField] internal string buildTimestampUtc = "";
    }
}
