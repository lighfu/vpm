// なぜ HD の private フィールドを書き換えるか: HierarchyDecorator (HD) には Info 配列
// (コンポーネントアイコン列を構成する部品の並び) へ外部から追加するための公開 API が無い。
// HierarchyManager.Info (private static, readonly ではない) に AntiRippingHierarchyInfo を
// 差し込む以外に、HD の枠を確保して重ならず並べる手段がないため、リフレクションで書き換える。
// AR 本体の API も型名からリフレクションで束ねる (このアセンブリは AR 本体をコンパイル時に参照しない)。
// どちらかが失敗した場合は何もせず、AntiRippingHierarchyHook 側の従来表示 (このブランチの自前描画) に戻る。

using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using HD = global::HierarchyDecorator;

namespace AjisaiFlow.AntiRipping.HierarchyDecorator
{
    [InitializeOnLoad]
    internal static class AntiRippingHierarchyDecoratorBridge
    {
        private const string RendererName = "HierarchyDecorator";
        private const string ApiAssemblyQualifiedTypeName =
            "AjisaiFlow.AntiRipping.Editor.Integration.HierarchyIconApi, AjisaiFlow.AntiRipping.Editor";

        internal delegate bool IsExternalRendererEnabledDelegate();
        internal delegate void ReportExternalRendererActiveDelegate(string rendererName);
        internal delegate void ReportExternalRendererFailedDelegate(string rendererName, string reason);
        internal delegate bool TryGetIconDelegate(int instanceId, out Texture icon, out Color tint, out string tooltip);
        internal delegate bool HandleClickDelegate(int instanceId, Rect iconRect);

        internal static IsExternalRendererEnabledDelegate IsExternalRendererEnabled;
        internal static ReportExternalRendererActiveDelegate ReportExternalRendererActive;
        internal static ReportExternalRendererFailedDelegate ReportExternalRendererFailed;
        internal static TryGetIconDelegate TryGetIcon;
        internal static HandleClickDelegate HandleClick;

        static AntiRippingHierarchyDecoratorBridge()
        {
            // HD は HierarchyManager に初めて触れるのを delayCall (HierarchyManager.Initialize) まで遅らせている。
            // ここで直接 Install() を呼ぶと、InitializeOnLoad の最中に HD の静的初期化を走らせてしまうので delayCall に回す。
            // HD の Initialize との前後は問わない (Initialize は Info 配列を作り直さない)。
            EditorApplication.delayCall -= Install;
            EditorApplication.delayCall += Install;
        }

        internal static void Install()
        {
            try
            {
                if (!BindApi())
                {
                    // AR 本体の API を 1 つでも取れなければ何もしない。AR 本体側は NotReported のまま
                    // (設定画面は「連携できませんでした」を表示し、アイコンは従来表示に戻る)。
                    return;
                }

                InstallInfo();
            }
            catch (Exception e)
            {
                SafeReportFailed(e.GetType().Name);
            }
        }

        private static void InstallInfo()
        {
            var infoField = typeof(HD.HierarchyManager).GetField("Info", BindingFlags.NonPublic | BindingFlags.Static);
            if (infoField == null)
            {
                ReportExternalRendererFailed(RendererName, "HierarchyManager.Info が見つからない");
                return;
            }

            if (!(infoField.GetValue(null) is HD.HierarchyInfo[] info) || info == null)
            {
                ReportExternalRendererFailed(RendererName, "HierarchyManager.Info が null");
                return;
            }

            for (var i = 0; i < info.Length; i++)
            {
                if (info[i] is AntiRippingHierarchyInfo)
                {
                    // 既に差し込み済み (冪等)。
                    ReportExternalRendererActive(RendererName);
                    return;
                }
            }

            // TagLayerInfo の後・ComponentIconInfo の前 (要件3)。ComponentIconInfo が見つからなければ末尾。
            var insertIndex = info.Length;
            for (var i = 0; i < info.Length; i++)
            {
                if (info[i] is HD.ComponentIconInfo)
                {
                    insertIndex = i;
                    break;
                }
            }

            var next = new HD.HierarchyInfo[info.Length + 1];
            Array.Copy(info, 0, next, 0, insertIndex);
            next[insertIndex] = new AntiRippingHierarchyInfo();
            Array.Copy(info, insertIndex, next, insertIndex + 1, info.Length - insertIndex);

            infoField.SetValue(null, next);

            ReportExternalRendererActive(RendererName);
            EditorApplication.RepaintHierarchyWindow();
        }

        private static bool BindApi()
        {
            var apiType = Type.GetType(ApiAssemblyQualifiedTypeName);
            if (apiType == null)
            {
                return false;
            }

            var isEnabledMethod = apiType.GetMethod("IsExternalRendererEnabled", BindingFlags.Public | BindingFlags.Static);
            var reportActiveMethod = apiType.GetMethod("ReportExternalRendererActive", BindingFlags.Public | BindingFlags.Static);
            var reportFailedMethod = apiType.GetMethod("ReportExternalRendererFailed", BindingFlags.Public | BindingFlags.Static);
            var tryGetIconMethod = apiType.GetMethod("TryGetIcon", BindingFlags.Public | BindingFlags.Static);
            var handleClickMethod = apiType.GetMethod("HandleClick", BindingFlags.Public | BindingFlags.Static);

            if (isEnabledMethod == null || reportActiveMethod == null || reportFailedMethod == null
                || tryGetIconMethod == null || handleClickMethod == null)
            {
                return false;
            }

            IsExternalRendererEnabled = (IsExternalRendererEnabledDelegate)Delegate.CreateDelegate(
                typeof(IsExternalRendererEnabledDelegate), isEnabledMethod);
            ReportExternalRendererActive = (ReportExternalRendererActiveDelegate)Delegate.CreateDelegate(
                typeof(ReportExternalRendererActiveDelegate), reportActiveMethod);
            ReportExternalRendererFailed = (ReportExternalRendererFailedDelegate)Delegate.CreateDelegate(
                typeof(ReportExternalRendererFailedDelegate), reportFailedMethod);
            TryGetIcon = (TryGetIconDelegate)Delegate.CreateDelegate(typeof(TryGetIconDelegate), tryGetIconMethod);
            HandleClick = (HandleClickDelegate)Delegate.CreateDelegate(typeof(HandleClickDelegate), handleClickMethod);

            return true;
        }

        private static void SafeReportFailed(string reason)
        {
            try
            {
                ReportExternalRendererFailed?.Invoke(RendererName, reason);
            }
            catch (Exception)
            {
                // API 経由の報告自体が失敗した場合は握りつぶす (Debug.Log は使わない)。
            }
        }
    }
}
