using System;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    // One session owns all capabilities. Commands select compiled business
    // branches; neither reflection names nor arbitrary paths cross the wire.
    internal sealed class DevelopmentModel
    {
        private readonly Model model;
        private readonly GrowthTaskModel growth = new GrowthTaskModel();
        private readonly EquipmentUpgradeModel equipment = new EquipmentUpgradeModel();
        private readonly SpiritBeastUpgradeModel spirit = new SpiritBeastUpgradeModel();
        private readonly SpiritTributeModel tribute = new SpiritTributeModel();
        private readonly StarActivationModel star = new StarActivationModel();
        private EquipmentUpgradeTarget equipmentTarget;
        private SpiritBeastUpgradeTarget spiritTarget;
        private SharedDevelopmentContext context;
        private string characterId = "", gate = "BLOCKED", reason = "not_observed", signature = "", lastCommand = "null";
        private DevelopmentCommand queued;
        private bool queuedMutation;
        internal int Revision;
        internal SharedDevelopmentContext CurrentContext { get { return context; } }
        internal bool Pending { get { return queued != null || growth.Pending || equipment.Pending || spirit.Pending || tribute.Pending || star.Pending; } }
        internal bool MutationAttempted { get { return queuedMutation || growth.MutationAttempted || equipment.MutationAttempted || spirit.MutationAttempted || tribute.MutationAttempted || star.MutationAttempted; } }
        internal DevelopmentModel(Model source) { model = source; }

        internal void Capture(bool campaignPending, bool verificationPending)
        {
            try
            {
                Snapshot common = model.Capture();
                string external = campaignPending ? "campaign_attempt_unresolved" : verificationPending ? "verification_pending" : DevelopmentContextReader.ExternalGate(common.Gate);
                context = DevelopmentContextReader.Read(model, common, external, false);
                characterId = context.CharacterId;
                growth.Capture(context); equipment.Capture(context, equipmentTarget); spirit.Capture(context, spiritTarget);
                try { tribute.Capture(context); }
                catch (Exception error) { tribute.CaptureFailed(context, error); }
                star.Capture(context);
                gate = context.Gate == "PASS" || growth.RewardReady ? "PASS" :
                    context.Gate == "controller_gate:loading_or_battle" || context.Gate == "controller_gate:global_input_disabled" ? "WAITING_UI" : "BLOCKED";
                reason = context.Gate == "PASS" || growth.RewardReady ? "" : context.Gate;
            }
            catch (Exception error) { gate = "BLOCKED"; reason = error is ProbeFailure ? error.Message : error.GetType().Name; context = null; }
            RefreshRevision();
        }
        private void RefreshRevision()
        {
            string current = ContentJson();
            if (signature != current)
            { if (Revision >= 999999999) throw new ProbeFailure("development_revision_exhausted"); Revision++; signature = current; }
        }
        internal void ObserveTarget(Command command)
        {
            if (command.DevelopmentAction == "equipment_observe") equipmentTarget = EquipmentTarget(command);
            else if (command.DevelopmentAction == "spirit_observe") spiritTarget = SpiritTarget(command);
            else if (command.DevelopmentAction == "growth_observe") growth.ObserveTarget(ConvertCommand(command));
            else if (command.DevelopmentAction == "star_observe") star.ObserveTarget(ConvertCommand(command));
            else if (command.DevelopmentAction == "tribute_observe") tribute.ObserveTarget(ConvertCommand(command));
        }
        private static EquipmentUpgradeTarget EquipmentTarget(Command c)
        { return new EquipmentUpgradeTarget { OperationId = c.OperationId, EquipmentId = c.TargetId, DesiredLevel = c.DesiredLevel, Mode = c.DevelopmentMode, MaxRequests = c.MaxRequests, MaxMoney = c.MaxMoney, MaxStone = c.MaxStone }; }
        private static SpiritBeastUpgradeTarget SpiritTarget(Command c)
        { return new SpiritBeastUpgradeTarget { OperationId = c.OperationId, DesiredLevel = c.DesiredLevel, MaxRequests = c.MaxRequests, MaxFood = c.MaxFood }; }
        internal static DevelopmentCommand ConvertCommand(Command c)
        { return new DevelopmentCommand { Sequence = c.Sequence, Action = c.DevelopmentAction, ExpectedRevision = c.ExpectedRevision, OperationId = c.OperationId, TargetId = c.TargetId, DesiredLevel = c.DesiredLevel, Mode = c.DevelopmentMode, MaxRequests = c.MaxRequests, MaxMoney = c.MaxMoney, MaxStone = c.MaxStone, MaxFood = c.MaxFood, MaxPractice = c.MaxPractice }; }
        private static bool Growth(string action) { return action.StartsWith("growth_"); }
        private static bool Spirit(string action) { return action.StartsWith("spirit_"); }
        private static bool Star(string action) { return action.StartsWith("star_"); }
        private static bool Tribute(string action) { return action.StartsWith("tribute_"); }
        internal bool Queue(Command command, out string why)
        {
            why = "";
            if (context == null || gate != "PASS" || command.ExpectedRevision != Revision || Pending)
            { why = "development_context_revision_or_pending"; Record(command, "REJECTED", why); return false; }
            DevelopmentCommand c = ConvertCommand(command);
            bool ok = Star(c.Action) ? star.Queue(c, out why) : Growth(c.Action) ? growth.Queue(c, out why) : Spirit(c.Action) ? spirit.Queue(c, out why) : Tribute(c.Action) ? tribute.Queue(c, out why) : equipment.Queue(c, out why);
            if (!ok) { Record(command, "REJECTED", why); return false; }
            queued = c;
            if (!Star(c.Action) && !Growth(c.Action) && !Spirit(c.Action) && !Tribute(c.Action)) equipmentTarget = EquipmentTarget(command);
            if (Spirit(c.Action)) spiritTarget = SpiritTarget(command);
            Record(command, "QUEUED", ""); return true;
        }
        internal bool ExecuteGui(Command command, DevelopmentPublication publish, out string why)
        {
            why = ""; DevelopmentCommand c = queued; queued = null;
            if (c == null || c.Sequence != command.Sequence || context == null || gate != "PASS")
            { why = "development_gui_not_owned"; Record(command, "NOT_INVOKED", why); Stop(why); return false; }
            bool invoked = Star(c.Action) ? star.ExecuteGui(c, publish, out why) : Growth(c.Action) ? growth.ExecuteGui(c, publish, out why) : Spirit(c.Action) ? spirit.ExecuteGui(c, publish, out why) : Tribute(c.Action) ? tribute.ExecuteGui(c, publish, out why) : equipment.ExecuteGui(c, publish, out why);
            queuedMutation = queuedMutation || growth.MutationAttempted || equipment.MutationAttempted || spirit.MutationAttempted || tribute.MutationAttempted || star.MutationAttempted;
            Record(command, invoked ? "INVOKED" : Pending ? "UNKNOWN" : "REJECTED", why); return invoked;
        }
        internal void NotInvoked(Command command, string why)
        { queued = null; growth.Stop(why); equipment.Stop(why); spirit.Stop(why); tribute.Stop(why); star.Stop(why); Record(command, "NOT_INVOKED", why); }
        internal void Failed(Command command, string why)
        { queued = null; growth.Stop(why); equipment.Stop(why); spirit.Stop(why); tribute.Stop(why); star.Stop(why); Record(command, "UNKNOWN", why); }
        private void Record(Command command, string status, string why)
        {
            lastCommand = "{\"sequence\":" + command.Sequence + ",\"action\":" + Command.Quote(command.DevelopmentAction) + ",\"operation_id\":" + Command.Quote(command.OperationId) + ",\"status\":" + Command.Quote(status) + ",\"reason\":" + Command.Quote(why) + "}";
            RefreshRevision();
        }
        internal void Stop(string why) { queued = null; growth.Stop(why); equipment.Stop(why); spirit.Stop(why); tribute.Stop(why); star.Stop(why); RefreshRevision(); }
        private string ContentJson()
        { return "\"schema\":1,\"character_id\":" + Command.Quote(characterId) + ",\"gate\":" + Command.Quote(gate) + ",\"reason\":" + Command.Quote(reason) + ",\"growth\":" + growth.Json() + ",\"equipment\":" + equipment.Json() + ",\"spirit\":" + spirit.Json() + ",\"tribute\":" + tribute.Json() + ",\"star\":" + star.Json() + ",\"last_command\":" + lastCommand; }
        internal string Json() { return characterId == "" ? "null" : "{\"revision\":" + Revision + "," + ContentJson() + "}"; }
    }
}
