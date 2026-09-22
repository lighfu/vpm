// HierarchyDecorator (HD) のコンポーネントアイコン列に AR のアイコンを「部品が 1 つ増えたように」
// 並べるための HierarchyInfo 派生。実際に HD.HierarchyManager.Info 配列へ差し込むのは
// AntiRippingHierarchyDecoratorBridge (HD に追加用の公開 API が無いため private フィールドを書き換える)。
// 差し込みが行われなければこの型はどこからも呼ばれず、AntiRippingHierarchyHook 側の従来表示に戻る。

using System;
using UnityEngine;
using HD = global::HierarchyDecorator;

namespace AjisaiFlow.AntiRipping.HierarchyDecorator
{
    /// <summary>
    /// HD の Info 配列に差し込む AR 用の部品。DrawerIsEnabled で表示可否と状態 (icon/tint/tooltip) を
    /// まとめて判定し、CalculateGridCount / DrawInfo は同じ Draw 呼び出し内でその結果だけを使う。
    ///
    /// DrawerIsEnabled / DrawInfo は HD の HierarchyManager.OnGUI から行ごとに呼ばれるため、
    /// ここで例外を投げると HD 全体の Hierarchy 描画が壊れる。両メソッドを try/catch で囲み、
    /// 最初の例外で <see cref="disabled"/> を立てて以後は自分自身を無効化する (DrawerIsEnabled は
    /// false を返し、HD 側の枠も取らない)。ExitGUIException は GUI 制御用の特別な例外なので
    /// 捕まえず再送出する。
    /// </summary>
    internal sealed class AntiRippingHierarchyInfo : HD.HierarchyInfo
    {
        private const string RendererName = "HierarchyDecorator";

        private bool hasIcon;
        private bool disabled;
        private int instanceId;
        private Texture icon;
        private Color tint;
        private string tooltip;

        protected override bool DrawerIsEnabled(HD.HierarchyItem item, HD.Settings settings)
        {
            hasIcon = false;

            if (disabled)
            {
                return false;
            }

            try
            {
                return DrawerIsEnabledCore(item, settings);
            }
            catch (ExitGUIException)
            {
                throw;
            }
            catch (Exception e)
            {
                Disable(e);
                return false;
            }
        }

        private bool DrawerIsEnabledCore(HD.HierarchyItem item, HD.Settings settings)
        {
            var isEnabled = AntiRippingHierarchyDecoratorBridge.IsExternalRendererEnabled;
            if (isEnabled == null || !isEnabled())
            {
                return false;
            }

            // HD の ComponentIconInfo.DrawerIsEnabled と同じ条件。コンポーネントアイコン列自体が
            // 非表示のときは AR のアイコンも出さない (要件4)。
            if (!settings.styleData.displayIcons && settings.styleData.HasStyle(item.DisplayName))
            {
                return false;
            }

            if (!settings.Components.Enabled)
            {
                return false;
            }

            var tryGetIcon = AntiRippingHierarchyDecoratorBridge.TryGetIcon;
            if (tryGetIcon == null)
            {
                return false;
            }

            var id = item.GameObject.GetInstanceID();
            if (!tryGetIcon(id, out icon, out tint, out tooltip))
            {
                return false;
            }

            instanceId = id;
            hasIcon = true;
            return true;
        }

        protected override int CalculateGridCount()
        {
            return hasIcon ? 1 : 0;
        }

        protected override void DrawInfo(Rect rect, HD.HierarchyItem item, HD.Settings settings)
        {
            if (disabled || !hasIcon || icon == null)
            {
                return;
            }

            try
            {
                DrawInfoCore(rect);
            }
            catch (ExitGUIException)
            {
                throw;
            }
            catch (Exception e)
            {
                Disable(e);
            }
        }

        private void DrawInfoCore(Rect rect)
        {
            var iconRect = new Rect(rect.x, rect.y, 16f, 16f);

            var prevColor = GUI.color;
            GUI.color = tint;
            GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit, true);
            GUI.color = prevColor;

            if (!string.IsNullOrEmpty(tooltip))
            {
                GUI.Label(iconRect, new GUIContent(string.Empty, tooltip), GUIStyle.none);
            }

            var handleClick = AntiRippingHierarchyDecoratorBridge.HandleClick;
            handleClick?.Invoke(instanceId, iconRect);
        }

        /// <summary>この部品を以後無効化し、AR 本体へ 1 回だけ失敗を報告する (e.Message は含めない)。</summary>
        private void Disable(Exception e)
        {
            if (disabled)
            {
                return;
            }

            disabled = true;

            try
            {
                AntiRippingHierarchyDecoratorBridge.ReportExternalRendererFailed?.Invoke(RendererName, e.GetType().Name);
            }
            catch (Exception)
            {
                // 報告そのものが投げても HD の OnGUI へ抜けないようにする (ブリッジの SafeReportFailed と同じ扱い)。
            }
        }
    }
}
