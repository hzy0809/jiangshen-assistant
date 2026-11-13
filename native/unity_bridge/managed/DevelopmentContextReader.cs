using System;
using System.Globalization;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal static class DevelopmentContextReader
    {
        internal static D Read(Model model, Snapshot common, string external, bool paused, bool requireSnapshotRoot = false)
        {
            UnityEngine.Object[] all = model.AllUi();
            Component root = model.CurrentRoot();
            if (requireSnapshotRoot && !ReferenceEquals(root, common.Root)) external = "not_current_root";
            GameObject owner = model.GlobalUiObject();
            Component globalUi = null;
            int count = 0;
            foreach (UnityEngine.Object item in all)
            {
                Component c = item as Component;
                if (c != null && c.GetType() == Model.GameType("RootUI") && c.gameObject == owner) { globalUi = c; count++; }
            }
            object character = common.Character ?? D.Static(Model.FirstType("Global"), "character_");
            string id = character == null ? "" : Convert.ToUInt64(D.Read(character, "id"), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            return new D(all, root, globalUi, character, id == "0" ? "" : id, count != 1 ? "global_ui_not_unique" : external, paused,
                delegate(Behaviour ui) { return OverlayGate(model, root, ui); });
        }

        internal static D DailyReward(Model model, bool blocked)
        { return ReadDailyPanel(model, blocked); }

        internal static D DailyStone(Model model, bool blocked)
        { return ReadDailyPanel(model, blocked); }

        internal static D DailyRefine(Model model, bool blocked)
        { return ReadDailyPanel(model, blocked); }

        internal static D DailyWood(Model model, bool blocked)
        { return ReadDailyPanel(model, blocked); }

        internal static D DailyArena(Model model, bool blocked)
        { return ReadDailyPanel(model, blocked); }

        internal static D DailySoul(Model model, bool blocked)
        { return ReadDailyPanel(model, blocked); }

        private static D ReadDailyPanel(Model model, bool blocked)
        {
            Snapshot common = model.Capture();
            string external = ExternalGate(common.Gate);
            // Stock loading and dialogs can disable global input. A common
            // capture that exits there has yet to verify these separate flags.
            if (external == "PASS" || external == "global_input_disabled")
            {
                if (common.Main == null || D.Bool(D.Read(common.Main, "first")) || !D.Bool(D.Read(common.Main, "m_bInit")))
                    external = "main_not_initialized";
                else if (D.Bool(D.Static(Model.GameType("BuildingBuildUI"), "m_bBuildVisible"))) external = "building_overlay";
                else if (D.Bool(D.Static(Model.FirstType("PopupUI"), "popup"))) external = "popup_active";
                else if (D.Bool(D.Static(Model.GameType("BuildingBehaviourScript"), "m_bEditVisible"))) external = "building_edit_visible";
                else if (external == "PASS" && !common.InteractionEnabled && D.Static(Model.GameType("MainUI"), "m_subui") == null) external = "main_interaction_disabled";
            }
            return Read(model, common, external, blocked, true);
        }

        internal static string ExternalGate(string current)
        {
            // Each capability separately assesses panel ownership. Keep the
            // common identity, loading and modal checks.
            return current == "PASS" || current == "subui_present" || current == "visible_ui_count" || current == "visible_unknown_ui" || current == "main_interaction_disabled" ? "PASS" : current;
        }

        private static string OverlayGate(Model model, Component root, Behaviour ui)
        {
            if (ui.GetType() == Model.GameType("CampaignUI")) return CampaignOwner(root, ui) ? null : "campaign_root_mismatch";
            string name = ui.GetType().FullName;
            if (name == "CampaignTroopScript" || name == "CampaignSignScript" || name == "CampaignChestScript")
            {
                Component parent = D.Read(ui, "ParentUI") as Component;
                return parent != null && parent.GetType() == Model.GameType("CampaignUI") && CampaignOwner(root, parent) ? null : "campaign_chrome_root_mismatch";
            }
            return model.OverlayGate(ui);
        }

        private static bool CampaignOwner(Component root, Component panel)
        { return root != null && ReferenceEquals(D.Read(panel, "rmg"), root) && panel.transform.root == root.transform && D.Read(root, "LevelID") as string == "Campaign"; }
    }
}
