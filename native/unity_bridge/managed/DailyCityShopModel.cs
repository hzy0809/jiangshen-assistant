using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    // Only the four money-shop templates are admitted. Recipes and ownership
    // come from the current character and current loaded templates, never the
    // investigation cache. One initial list, one harvest/production per row.
    internal sealed class DailyCityShopModel
    {
        internal const int TaskId = 308;
        private const ushort HarvestOp = 1154, ProduceOp = 1158;
        private const uint WaitMs = 22000;
        private static readonly Type[] ReplyArgs = { typeof(MemoryStream), typeof(object) };

        private sealed class Window
        {
            internal Component Ui;
            internal DateTime Shown;
            private readonly string operation;
            private readonly int openingSequence;
            private readonly Candidate target;
            internal Window(Component ui, DailyCityShopModel owner)
            {
                Ui = ui; Shown = (DateTime)D.Read(ui, "visible_time");
                operation = owner.operationId; openingSequence = owner.sequence; target = owner.target;
            }
            internal bool Same(Component ui, DailyCityShopModel owner)
            {
                return ReferenceEquals(Ui, ui) && operation != "" && operation == owner.operationId &&
                    openingSequence > 0 && openingSequence <= owner.sequence && ReferenceEquals(target, owner.target) &&
                    Shown == (DateTime)D.Read(ui, "visible_time");
            }
        }

        private sealed class Frame
        {
            internal D Context;
            internal DailyTransport Transport;
            internal object Character, Client, Sub;
            internal Component Root, Capital, Harvest, Product, Reward;
            internal ulong Role;
            internal uint Now, Cycle, GoldConsumed;
            internal int Gold, Bound, Mana;
            internal long Money;
            internal string Scene, Clock;
        }

        private sealed class Quote
        {
            internal object Product, Template;
            internal uint Id, Time, Mana, Money;
            internal bool Same(Quote q)
            { return q != null && ReferenceEquals(Product, q.Product) && ReferenceEquals(Template, q.Template) && Id == q.Id && Time == q.Time && Mana == q.Mana && Money == q.Money; }
        }

        private sealed class Candidate
        {
            internal object Building, Data, InitialProduct;
            internal Component Source;
            internal GameObject Mesh;
            internal uint Id, Template, Island, Level, InitialStart, InitialProductId;
            internal bool InitiallyReady, InitiallyIdle, Collected, Produced, ProductionSkipped;
            internal Quote Quote;
            internal Receipt HarvestReceipt;
        }

        private sealed class Skip
        {
            internal uint Id, Template;
            internal string Stage, Reason;
            internal bool Limited;
            internal string Json()
            { return "{\"building_id\":" + Id + ",\"building_template_id\":" + Template + ",\"stage\":" + D.Q(Stage) + ",\"reason\":" + D.Q(Reason) + ",\"limits_request\":" + D.B(Limited) + "}"; }
        }

        private sealed class Receipt
        {
            internal string Operation, Action, Hash = "", Error = "", Callback = "";
            internal int Sequence, SourceRevision, Opcode, SourceInstance, RootInstance;
            internal uint Building, Template, Product, Duration, Result, ResponseBuilding, ResponseProduct, Start, Gold, Mana, NominalMoney;
            internal long Money;
            internal bool Observed, Lucky, Immediate, Returned, Success, State, RewardClosed, ProductCleared, UiClosed, MoneyCredited;
            internal string Json()
            {
                return "{\"operation_id\":" + D.Q(Operation) + ",\"sequence\":" + Sequence + ",\"source_revision\":" + SourceRevision +
                    ",\"action\":" + D.Q(Action) + ",\"opcode\":" + Opcode + ",\"source_instance_id\":" + SourceInstance +
                    ",\"root_instance_id\":" + RootInstance + ",\"stock_callback\":" + D.Q(Callback) +
                    ",\"building_id\":" + Building + ",\"building_template_id\":" + Template + ",\"product_id\":" + Product +
                    ",\"duration_seconds\":" + Duration + ",\"response_observed\":" + D.B(Observed) + ",\"result\":" + (Observed ? Result.ToString(CultureInfo.InvariantCulture) : "null") +
                    ",\"response_building_id\":" + ResponseBuilding + ",\"response_product_id\":" + ResponseProduct + ",\"start_time\":" + Start +
                    ",\"lucky\":" + D.B(Lucky) + ",\"is_immediate\":" + D.B(Immediate) + ",\"gold_cost\":" + Gold + ",\"mana_cost\":" + Mana +
                    ",\"nominal_money_output\":" + NominalMoney + ",\"money_received\":" + Money + ",\"money_credited\":" + D.B(MoneyCredited) +
                    ",\"receipt_success\":" + D.B(Success) + ",\"stock_callback_returned\":" + D.B(Returned) + ",\"state_confirmed\":" + D.B(State) +
                    ",\"reward_closed\":" + D.B(RewardClosed) + ",\"product_cleared\":" + D.B(ProductCleared) + ",\"ui_closed\":" + D.B(UiClosed) +
                    ",\"response_sha256\":" + D.Q(Hash) + ",\"error\":" + D.Q(Error) + "}";
            }
        }

        private sealed class Intent
        {
            internal string Action;
            internal Frame Before;
            internal Candidate Target;
            internal Quote Quote;
            internal int Revision;
        }

        private Model model;
        private Frame frame, origin;
        private Component capitalRoot, ownedReward;
        private Window harvestWindow, productWindow, rewardWindow;
        private DateTime previousHarvestShown, previousProductShown;
        private object rewardBonuses;
        private Delegate rewardClose;
        private readonly List<Candidate> candidates = new List<Candidate>();
        private readonly List<Receipt> receipts = new List<Receipt>();
        private readonly List<Skip> skips = new List<Skip>();
        private Candidate target;
        private Candidate productOpeningTarget;
        private Intent queued;
        private Watch watch;
        private int sequence, notInvokedSequence, cursor, since, duration, productOpeningSequence;
        private bool hold, stopped, terminal, attempted, observationAvailable, limited, cleanupVerified;
        private string operationId = "", status = "IDLE", phase = "", reason = "", gate = "shop_observation_unavailable", stamp = "";
        private string cleanupAction = "", cleanupReason = "", failureReason = "";

        internal int Revision { get; private set; }
        internal bool MutationAttempted { get { return attempted; } }
        internal bool GuiInvocationAttempted { get; private set; }
        internal bool OutcomeUnknown { get { return status == "UNKNOWN"; } }
        internal bool Busy { get { return queued != null || hold || watch != null; } }

        internal void Capture(Model currentModel, int taskId, bool admissionBlocked)
        {
            if (taskId != TaskId) throw new ArgumentOutOfRangeException("taskId");
            model = currentModel;
            try
            {
                frame = Read(admissionBlocked);
                observationAvailable = true;
                if (hold && !Owner(frame)) throw new ProbeFailure("shop_owner_or_cycle_changed");
                if (watch != null && watch.Error != "") throw new ProbeFailure(watch.Error);
                if (terminal || stopped || status == "UNKNOWN")
                {
                    cleanupAction = "";
                    if (cleanupVerified) cleanupVerified = FreeGate(frame) == "PASS";
                    if (status == "UNKNOWN" && !terminal && !stopped && hold)
                    {
                        cleanupReason = ProductCleanupGate(frame, true);
                        if (cleanupReason == "PASS") cleanupAction = "close_product";
                    }
                    Publish(); return;
                }
                if (!hold)
                {
                    gate = StartGate(frame); reason = gate == "PASS" ? "" : gate;
                    status = "IDLE"; Publish(); return;
                }
                if (frame.Clock != "") { gate = frame.Clock; Publish(); return; }
                if (frame.Gold != origin.Gold || frame.Bound != origin.Bound || frame.GoldConsumed != origin.GoldConsumed)
                    throw new ProbeFailure("shop_premium_balance_changed");
                if (unchecked((uint)(Environment.TickCount - since)) > WaitMs &&
                    (phase == "opening" || phase == "harvest_loading" || phase == "product_loading" || phase == "reward_loading" ||
                        phase == "waiting_collect" || phase == "waiting_produce"))
                    throw new ProbeFailure("shop_owned_step_timeout");

                if (phase == "opening")
                {
                    status = "OPENING";
                    if (frame.Scene != "Capital" || FreeGate(frame) != "PASS") { gate = "shop_waiting_capital"; Publish(); return; }
                    capitalRoot = frame.Root;
                    Freeze(frame);
                    phase = "selecting";
                }
                if (phase == "waiting_collect")
                {
                    status = "PENDING"; gate = "shop_waiting_1154";
                    if (watch != null && watch.Receipt.Success && watch.Receipt.Returned)
                    {
                        target.HarvestReceipt = watch.Receipt;
                        if (!watch.Restore()) throw new ProbeFailure("shop_harvest_handler_cleanup_unverified");
                        watch = null; phase = "reward_loading"; since = Environment.TickCount;
                    }
                }
                if (phase == "reward_loading" || phase == "reward")
                {
                    status = "REWARD"; gate = "shop_waiting_owned_reward";
                    if (!OwnReward(frame)) throw new ProbeFailure("shop_reward_ownership_changed");
                    if (Ready(ownedReward))
                    {
                        if (frame.Money != checked(target.HarvestReceipt.Money + rewardMoneyBefore))
                            throw new ProbeFailure("shop_money_credit_unverified");
                        target.HarvestReceipt.MoneyCredited = true;
                        if (rewardWindow == null) rewardWindow = new Window(ownedReward, this);
                        else if (!rewardWindow.Same(ownedReward, this)) throw new ProbeFailure("shop_reward_generation_changed");
                        phase = "reward"; gate = RewardGate(frame);
                    }
                }
                if (phase == "harvest_loading")
                {
                    status = "OPENING"; gate = "shop_waiting_harvest_panel";
                    if (!ReferenceEquals(D.Read(frame.Harvest, "building"), target.Building)) throw new ProbeFailure("shop_harvest_panel_target_changed");
                    if (Ready(frame.Harvest))
                    {
                        if ((DateTime)D.Read(frame.Harvest, "visible_time") <= previousHarvestShown) throw new ProbeFailure("shop_harvest_generation_unverified");
                        harvestWindow = new Window(frame.Harvest, this); phase = "harvest_ready";
                    }
                }
                if (phase == "harvest_ready") { status = "READY"; gate = HarvestGate(frame); }
                if (phase == "product_loading")
                {
                    status = "OPENING"; gate = "shop_waiting_product_panel";
                    if (!ReferenceEquals(D.Read(frame.Product, "building"), target.Building) || !Flag(frame.Product, "m_bSub") || D.Read(frame.Product, "OnClose") != null)
                        throw new ProbeFailure("shop_product_panel_target_changed");
                    if (Ready(frame.Product))
                    {
                        if ((DateTime)D.Read(frame.Product, "visible_time") <= previousProductShown) throw new ProbeFailure("shop_product_generation_unverified");
                        productWindow = new Window(frame.Product, this); phase = "product_ready";
                        if (duration != 0)
                        {
                            Quote quote;
                            string why = ProductionQuote(frame, target, out quote);
                            if (why != "") { SkipProduction(why); }
                            else target.Quote = quote;
                        }
                    }
                }
                if (phase == "product_ready")
                {
                    status = "READY"; gate = ProductGate(frame);
                    if (gate == "PASS" && duration != 0 && !target.ProductionSkipped)
                    {
                        Quote quote;
                        string why = ProductionQuote(frame, target, out quote);
                        if (why != "") SkipProduction(why);
                        else if (!target.Quote.Same(quote)) SkipProduction("shop_recipe_quote_changed");
                    }
                }
                if (phase == "waiting_produce")
                {
                    status = "PENDING"; gate = "shop_waiting_1158";
                    if (watch != null && watch.Receipt.Success && watch.Receipt.Returned)
                    {
                        if (!ProducedState(frame, watch.Receipt, watch.Before)) throw new ProbeFailure("shop_production_state_changed");
                        watch.Receipt.UiClosed = true; target.Produced = true;
                        if (!watch.Restore()) throw new ProbeFailure("shop_product_handler_cleanup_unverified");
                        watch = null; Advance();
                    }
                }
                if (phase == "selecting") SelectNext(frame);
                if (phase == "ready_finish") { status = "READY"; gate = FreeGate(frame); }
                Publish();
            }
            catch (Exception error)
            {
                observationAvailable = false;
                string detail = error is ProbeFailure ? error.Message : "shop_observation_schema_unavailable";
                if (hold || attempted) Unknown(detail);
                else { status = "UNAVAILABLE"; reason = gate = detail; }
                Publish();
            }
        }

        private long rewardMoneyBefore;

        internal bool Queue(int taskId, ulong role, string id, int seq, int expectedRevision, string action, out string why)
        {
            why = "shop_admission";
            if (taskId != TaskId || stopped || terminal || queued != null || frame == null || !observationAvailable || role == 0 || role != frame.Role ||
                expectedRevision != Revision || seq <= sequence || !new DevelopmentCommand { OperationId = id }.ValidId() ||
                (operationId != "" && operationId != id)) return false;
            if (!Monitor.TryEnter(frame.Client, 0)) { why = "shop_client_busy"; return false; }
            try
            {
                why = Admission(frame, action);
                if (why != "") return false;
                queued = new Intent { Action = action, Before = frame, Target = target, Quote = target == null ? null : target.Quote, Revision = expectedRevision };
                operationId = id; sequence = seq; notInvokedSequence = 0; GuiInvocationAttempted = false;
                Publish(); return true;
            }
            finally { Monitor.Exit(frame.Client); }
        }

        internal bool ExecuteGui(DevelopmentPublication publish, DailyMonsterInvocationGate begin, out string why)
        {
            why = "shop_not_queued";
            Intent intent = queued;
            if (intent == null || stopped) return false;
            object client = intent.Before.Client;
            if (!D.Gui || publish == null || begin == null || !Monitor.TryEnter(client, 0)) return Reject("shop_gui_or_client_busy", out why);
            bool publishing = false;
            try
            {
                Frame fresh = Read(false);
                why = Recheck(intent, fresh);
                if (why != "") return Reject(why, out why);
                publishing = true; publish(); publishing = false;
                fresh = Read(false);
                why = Recheck(intent, fresh);
                if (why != "" || !begin() || stopped || queued != intent) return Reject(why == "" ? "shop_cancelled_before_call" : why, out why);
                fresh = Read(false);
                why = Recheck(intent, fresh);
                if (why != "") return Reject(why, out why);
                queued = null; GuiInvocationAttempted = true; since = Environment.TickCount;
                Execute(intent, fresh);
                frame = Read(false);
                Publish(); why = ""; return true;
            }
            catch (Exception error)
            {
                if (publishing) throw;
                observationAvailable = false;
                queued = null;
                why = error is ProbeFailure ? error.Message : "shop_stock_invocation_unverified";
                if (GuiInvocationAttempted || hold || attempted) Unknown(why);
                else { status = "BLOCKED"; reason = gate = why; terminal = true; }
                Publish(); return false;
            }
            finally { Monitor.Exit(client); }
        }

        private bool Reject(string detail, out string why)
        {
            queued = null; why = reason = gate = detail;
            if (hold || attempted) Unknown(detail);
            else { status = "BLOCKED"; terminal = true; }
            Publish(); return false;
        }

        private string Recheck(Intent intent, Frame f)
        {
            if (queued != intent || stopped || !D.Gui || !SameAccount(intent.Before, f) ||
                !ReferenceEquals(intent.Before.Root, f.Root) || !ReferenceEquals(intent.Target, target) ||
                intent.Before.Gold != f.Gold || intent.Before.Bound != f.Bound || intent.Before.GoldConsumed != f.GoldConsumed ||
                intent.Before.Mana != f.Mana || intent.Before.Money != f.Money) return "shop_command_baseline_changed";
            if (intent.Quote != null && target != null && !ReferenceEquals(intent.Quote, target.Quote)) return "shop_quote_owner_changed";
            return Admission(f, intent.Action);
        }

        private void Execute(Intent intent, Frame f)
        {
            string action = intent.Action;
            if (!hold)
            {
                duration = Duration(action); origin = f; hold = true; phase = "opening"; status = "OPENING"; gate = "shop_waiting_capital";
                if (f.Scene != "Capital") D.MainBranch("_capital");
                return;
            }
            if (action == "open_harvest")
            {
                previousHarvestShown = (DateTime)D.Read(f.Harvest, "visible_time"); harvestWindow = null;
                phase = "harvest_loading"; status = "OPENING";
                D.Method(f.Harvest.GetType(), "MyLoading", new[] { Model.FirstType("Database.Building") }, false, typeof(void)).Invoke(f.Harvest, new[] { target.Building });
            }
            else if (action == "collect")
            {
                rewardClose = Delegate.CreateDelegate(CloseType(), f.Harvest,
                    D.Method(f.Harvest.GetType(), "HarvestOK", Type.EmptyTypes, false, typeof(void)));
                Receipt receipt = NewReceipt(intent, f, HarvestOp, target.InitialProduct, f.Harvest);
                watch = new Watch(this, f, target, receipt, rewardClose);
                attempted = true; phase = "waiting_collect"; status = "PENDING";
                D.Call(f.Harvest, "DisableAll");
                D.Method(f.Harvest.GetType(), "wantHarvest", new[] { Model.FirstType("Database.Building"), CloseType(), typeof(bool) }, true, typeof(void))
                    .Invoke(null, new object[] { target.Building, rewardClose, false });
                watch.Attach(f.Harvest, "sc_building_harvest_0x482", true);
            }
            else if (action == "close_reward")
            {
                Receipt receipt = target.HarvestReceipt;
                if (!receipt.MoneyCredited || !OwnReward(f)) throw new ProbeFailure("shop_reward_close_unverified");
                previousProductShown = (DateTime)D.Read(f.Product, "visible_time"); productWindow = null;
                productOpeningTarget = target; productOpeningSequence = sequence;
                // This is the stock ordinary result button: Hide, then the
                // exact HarvestOK callback installed by this 1154 request.
                D.Call(ownedReward, "Hide"); rewardClose.DynamicInvoke(new object[0]);
                Frame after = Read(false);
                if (!Hidden(ownedReward) || !Hidden(after.Harvest) || D.Read(after.Harvest, "building") != null ||
                    UInt(target.Building, "idproduct") != 0 || D.Read(target.Building, "product") != null ||
                    !ReferenceEquals(D.Read(after.Product, "building"), target.Building) || !Flag(after.Product, "m_bSub") || D.Read(after.Product, "OnClose") != null)
                    throw new ProbeFailure("shop_stock_harvest_close_unverified");
                receipt.RewardClosed = receipt.ProductCleared = receipt.UiClosed = true;
                target.Collected = true; ownedReward = null; rewardBonuses = null; rewardWindow = null; rewardClose = null;
                phase = "product_loading"; status = "OPENING";
            }
            else if (action == "open_product")
            {
                previousProductShown = (DateTime)D.Read(f.Product, "visible_time"); productWindow = null;
                productOpeningTarget = target; productOpeningSequence = sequence;
                phase = "product_loading"; status = "OPENING";
                D.Method(f.Product.GetType(), "MyLoading", new[] { Model.FirstType("Database.Building"), typeof(bool), CloseType() }, false, typeof(void))
                    .Invoke(f.Product, new object[] { target.Building, true, null });
            }
            else if (action == "close_product")
            {
                bool failed = status == "UNKNOWN";
                D.Call(f.Product, "MyClose");
                Frame after = Read(false);
                if (!Hidden(after.Product) || D.Read(after.Product, "building") != null || after.Sub != null)
                    throw new ProbeFailure("shop_product_close_unverified");
                if (failed)
                {
                    if (FreeGate(after) != "PASS") throw new ProbeFailure("shop_failed_cleanup_unverified");
                    hold = false; terminal = cleanupVerified = true; cleanupAction = "";
                    cleanupReason = "PASS"; phase = "failed_closed";
                }
                else Advance();
            }
            else if (Duration(action) > 0)
            {
                Quote quote;
                string why = ProductionQuote(f, target, out quote);
                if (why != "" || !target.Quote.Same(quote)) throw new ProbeFailure(why == "" ? "shop_recipe_quote_changed" : why);
                FieldInfo selector = ProductSelector(f.Product, quote.Product);
                MethodInfo produce = D.Method(f.Product.GetType(), "ProduceOK", new[] { typeof(bool), typeof(object[]) }, false, typeof(void));
                Receipt receipt = NewReceipt(intent, f, ProduceOp, quote.Product, f.Product);
                watch = new Watch(this, f, target, receipt, null);
                SelectProduct(f.Product, quote.Product, selector);
                // Selection is the stock product-row selector. No preference,
                // resource, building state or immediate flag is written here.
                if (Flag(f.Character, "building_immediate")) throw new ProbeFailure("shop_paid_immediate_enabled");
                D.Call(f.Product, "DisableAll"); attempted = watch.InvocationAttempted = true; phase = "waiting_produce"; status = "PENDING";
                produce.Invoke(f.Product, new object[] { true, null });
                watch.Attach(f.Product, "sc_building_produce_0x486", false);
            }
            else if (action == "finish")
            {
                if (watch != null || FreeGate(f) != "PASS") throw new ProbeFailure("shop_finish_cleanup_unverified");
                hold = false; terminal = cleanupVerified = true; phase = "finished"; gate = "PASS";
                bool done = CollectedCount() + ProducedCount() > 0;
                status = limited ? (done ? "PARTIAL" : "UNAVAILABLE") : done ? "COMPLETED" : "NO_FREE_REWARD";
                reason = limited ? FirstLimitedReason() : done ? "shop_initial_list_complete" : "shop_no_current_work";
            }
            else throw new ProbeFailure("shop_action_unsupported");
        }

        private Receipt NewReceipt(Intent intent, Frame f, ushort opcode, object product, Component source)
        {
            Receipt receipt = new Receipt { Operation = operationId, Action = intent.Action, Sequence = sequence, SourceRevision = intent.Revision,
                Opcode = opcode, SourceInstance = source.GetInstanceID(), RootInstance = f.Root.GetInstanceID(), Building = target.Id, Template = target.Template,
                Product = UInt(product, "id"), Duration = UInt(product, "time"), NominalMoney = UInt(product, "money_output") };
            receipts.Add(receipt); return receipt;
        }

        private string Admission(Frame f, string action)
        {
            if (stopped || terminal || queued != null && queued.Action != action) return "shop_action_unavailable";
            if (status == "UNKNOWN") return action == "close_product" && cleanupAction == action ? ProductCleanupGate(f, false) == "PASS" ? "" : "shop_failed_cleanup_gate_changed" : "shop_action_unavailable";
            if (!hold) return (action == "open" || Duration(action) > 0) && StartGate(f) == "PASS" ? "" : "shop_start_gate";
            if (!Owner(f)) return "shop_owner_or_cycle_changed";
            if (f.Clock != "") return f.Clock;
            if (gate != "PASS") return gate;
            string offered = Offer(true);
            if (offered == "" || action != offered) return "shop_action_changed";
            if (action == "finish") return FreeGate(f) == "PASS" ? "" : "shop_cleanup_pending";
            if (target == null || !TargetCurrent(f, target)) return "shop_candidate_changed";
            if (action == "open_harvest") return FreeGate(f) == "PASS" && Harvestable(f, target) ? "" : "shop_harvest_not_ready";
            if (action == "collect") return HarvestGate(f) == "PASS" && Harvestable(f, target) ? "" : "shop_harvest_gate_changed";
            if (action == "close_reward") return RewardGate(f) == "PASS" && target.HarvestReceipt.MoneyCredited &&
                f.Money == checked(rewardMoneyBefore + target.HarvestReceipt.Money) ? "" : "shop_reward_gate_changed";
            if (action == "open_product")
            {
                Quote quote;
                string why = ProductionQuote(f, target, out quote);
                return FreeGate(f) == "PASS" && why == "" && target.Quote.Same(quote) ? "" : why == "" ? "shop_product_quote_changed" : why;
            }
            if (action == "close_product") return ProductGate(f) == "PASS" ? "" : "shop_product_close_gate_changed";
            if (Duration(action) != duration || ProductGate(f) != "PASS") return "shop_duration_or_product_gate_changed";
            Quote current;
            string failure = ProductionQuote(f, target, out current);
            return failure != "" ? failure : target.Quote.Same(current) ? "" : "shop_recipe_quote_changed";
        }

        private string Offer(bool admitted = false)
        {
            if (stopped || terminal || queued != null && !admitted || !observationAvailable) return "";
            if (status == "UNKNOWN") return cleanupAction;
            if (gate != "PASS") return "";
            if (!hold) return status == "IDLE" ? "open" : "";
            if (phase == "ready_finish") return "finish";
            if (phase == "selected_harvest") return "open_harvest";
            if (phase == "harvest_ready") return "collect";
            if (phase == "reward") return "close_reward";
            if (phase == "selected_product") return "open_product";
            if (phase == "product_ready") return duration == 0 || target.ProductionSkipped ? "close_product" : ProductionAction();
            return "";
        }

        private void Freeze(Frame f)
        {
            IDictionary rows = Map(D.Read(f.Character, "buildings"), 256);
            foreach (DictionaryEntry entry in rows)
            {
                object building = entry.Value;
                uint template = UInt(building, "idbuilding");
                if (!MoneyShop(template)) continue;
                uint id = UInt(building, "id");
                // Instance IDs are byte dictionary keys. Stock addBuilding and
                // getBuilding accept key 0; only the product ID has an idle
                // sentinel. Do not impose a positive instance-ID convention.
                if (id > Byte.MaxValue || D.UInt(entry.Key) != id || building.GetType() != Model.FirstType("Database.Building"))
                    throw new ProbeFailure("shop_owned_building_schema");
                object product = D.Read(building, "product");
                Candidate c = new Candidate { Building = building, Data = D.Read(building, "data"), Id = id, Template = template,
                    Island = UInt(building, "idisland"), Level = UInt(building, "level"), InitialProduct = product,
                    InitialProductId = UInt(building, "idproduct"), InitialStart = UInt(building, "start_time") };
                c.Mesh = D.Read(building, "mesh") as GameObject;
                if (c.Mesh != null) c.Source = c.Mesh.GetComponent(Model.GameType("BuildingBehaviourScript"));
                c.InitiallyIdle = product == null && c.InitialProductId == 0;
                c.InitiallyReady = product != null && c.InitialProductId == UInt(product, "id") && UInt(product, "time") > 0 &&
                    (ulong)c.InitialStart + UInt(product, "time") <= f.Now;
                candidates.Add(c);
            }
            candidates.Sort(delegate(Candidate a, Candidate b) { return a.Id.CompareTo(b.Id); });
        }

        private void SelectNext(Frame f)
        {
            target = null; harvestWindow = productWindow = null;
            while (cursor < candidates.Count)
            {
                Candidate c = candidates[cursor];
                string why = BuildingGate(f, c);
                if (why != "") { AddSkip(c, "building", why, true); cursor++; continue; }
                if (!c.InitiallyIdle && !c.InitiallyReady)
                {
                    AddSkip(c, "initial", c.InitialProduct != null ? "shop_already_producing" : "shop_initial_product_inconsistent", c.InitialProduct == null);
                    cursor++; continue;
                }
                if (c.InitiallyReady)
                {
                    if (!Harvestable(f, c)) { AddSkip(c, "collect", "shop_initial_harvest_changed", true); cursor++; continue; }
                    target = c; phase = "selected_harvest"; status = "READY"; gate = FreeGate(f); return;
                }
                if (duration == 0) { AddSkip(c, "collect", "shop_collect_only_idle", false); cursor++; continue; }
                Quote quote;
                why = ProductionQuote(f, c, out quote);
                if (why != "") { AddSkip(c, "produce", why, true); cursor++; continue; }
                c.Quote = quote; target = c; phase = "selected_product"; status = "READY"; gate = FreeGate(f); return;
            }
            phase = "ready_finish"; status = "READY"; gate = FreeGate(f);
        }

        private void Advance()
        { cursor++; target = null; harvestWindow = productWindow = null; phase = "selecting"; status = "READY"; gate = "shop_waiting_observation"; since = Environment.TickCount; }

        private void AddSkip(Candidate c, string stage, string why, bool limits)
        { skips.Add(new Skip { Id = c.Id, Template = c.Template, Stage = stage, Reason = why, Limited = limits }); limited |= limits; }
        private void SkipProduction(string why)
        { if (!target.ProductionSkipped) AddSkip(target, "produce", why, true); target.ProductionSkipped = true; }
        private string FirstLimitedReason()
        { foreach (Skip row in skips) if (row.Limited) return row.Reason; return "shop_requested_work_unavailable"; }

        private bool TargetCurrent(Frame f, Candidate c)
        {
            IDictionary rows = Map(D.Read(f.Character, "buildings"), 256);
            if (!ReferenceEquals(rows[(byte)c.Id], c.Building) || UInt(c.Building, "id") != c.Id || UInt(c.Building, "idbuilding") != c.Template ||
                !ReferenceEquals(D.Read(c.Building, "data"), c.Data) || UInt(c.Building, "idisland") != c.Island || UInt(c.Building, "level") != c.Level) return false;
            return c.Mesh != null && c.Source != null && c.Mesh.activeInHierarchy && c.Mesh.transform.root == f.Root.transform &&
                ReferenceEquals(D.Read(c.Building, "mesh"), c.Mesh) && c.Source.gameObject == c.Mesh &&
                c.Mesh.GetComponents(Model.GameType("BuildingBehaviourScript")).Length == 1 &&
                ReferenceEquals(D.Read(c.Source, "data"), c.Building) && !Flag(c.Source, "m_bFriend") && !Flag(c.Source, "m_bEdit") &&
                Flag(c.Source, "detectMouseClick") && ReferenceEquals(D.Read(c.Source, "rmg"), f.Root);
        }

        private string BuildingGate(Frame f, Candidate c)
        {
            if (!TargetCurrent(f, c)) return "shop_building_source_unavailable";
            if (!MoneyShop(c.Template) || c.Data == null || UInt(c.Data, "id") != c.Template || UInt(c.Data, "type") != 5 || UInt(c.Data, "automatic") != 1 ||
                !ReferenceEquals(c.Data, D.Template("building", c.Template))) return "shop_money_template_changed";
            if (Flag(c.Building, "notbuild") || UInt(c.Building, "build_time") != 0) return "shop_building_not_complete";
            if (UInt(c.Building, "upgrade_finish") > f.Now) return "shop_upgrade_in_progress";
            if (UInt(c.Building, "cd_time") > f.Now) return "shop_building_cooldown";
            int matches = 0; bool unlocked = false;
            foreach (DictionaryEntry item in Map(D.Read(f.Character, "building_areas"), 64))
            {
                object area = item.Value;
                if (!Map(D.Read(area, "m_islands"), 256).Contains((byte)c.Island)) continue;
                matches++; unlocked = Flag(area, "unlocked");
            }
            return c.Island == 0 || matches != 1 || !unlocked ? "shop_building_area_locked_or_unknown" : "";
        }

        private bool Harvestable(Frame f, Candidate c)
        {
            if (BuildingGate(f, c) != "" || !c.InitiallyReady || c.Collected ||
                !ReferenceEquals(D.Read(c.Building, "product"), c.InitialProduct) || UInt(c.Building, "idproduct") != c.InitialProductId ||
                UInt(c.Building, "start_time") != c.InitialStart) return false;
            object p = c.InitialProduct;
            return p != null && UInt(p, "id") == c.InitialProductId && UInt(p, "idbuilding") == c.Template && UInt(p, "time") > 0 &&
                (ulong)c.InitialStart + UInt(p, "time") <= f.Now && UInt(p, "money_output") > 0 && OnlyMoney(p);
        }

        private string ProductionQuote(Frame f, Candidate c, out Quote quote)
        {
            try { return ReadProductionQuote(f, c, out quote); }
            catch (Exception error)
            {
                quote = null;
                return error is ProbeFailure ? "shop_recipe_field_unavailable:" + error.Message : "shop_recipe_quote_schema_unavailable";
            }
        }

        private string ReadProductionQuote(Frame f, Candidate c, out Quote quote)
        {
            quote = null;
            string why = BuildingGate(f, c);
            if (why != "") return why;
            if (Flag(f.Character, "building_immediate")) return "shop_paid_immediate_enabled";
            if (D.Read(c.Building, "product") != null || UInt(c.Building, "idproduct") != 0 || c.Produced) return "shop_building_not_idle";
            if (duration != 1200 && duration != 3600 && duration != 43200) return "shop_duration_not_selected";
            IList products = List(D.Read(c.Building, "m_products"), 64);
            foreach (object p in products)
            {
                if (UInt(p, "time") != duration) continue;
                if (quote != null) return "shop_recipe_duration_ambiguous";
                uint id = UInt(p, "id");
                foreach (string cost in new[] { "gold_consume", "wood_consume", "mine_consume" })
                    if (UInt(p, cost) != 0) return "shop_recipe_cost_not_zero:" + cost;
                if (p.GetType() != Model.FirstType("Database.BuildingProduct") || id == 0 || id > Byte.MaxValue || UInt(p, "idbuilding") != c.Template ||
                    UInt(p, "money_output") == 0 || !OnlyMoney(p) ||
                    UInt(p, "demand_consume") != 0 || UInt(p, "demand_type") != 0 || UInt(p, "population") != 0 || UInt(p, "prebuilding") != 0)
                    return "shop_recipe_not_ordinary_money";
                object template = D.Template("building_product", id);
                if (!SameRecipe(p, template)) return "shop_runtime_recipe_template_mismatch";
                int belongs = 0;
                foreach (object entry in List(D.Read(c.Data, "m_products"), 64)) if (UInt(entry, "id") == id && SameRecipe(entry, p)) belongs++;
                if (belongs != 1) return "shop_recipe_building_membership_unverified";
                quote = new Quote { Product = p, Template = template, Id = id, Time = UInt(p, "time"), Mana = UInt(p, "mana_consume"), Money = UInt(p, "money_output") };
            }
            if (quote == null) return "shop_selected_duration_recipe_missing";
            return quote.Mana > Int32.MaxValue || f.Mana < 0 || (uint)f.Mana < quote.Mana ? "shop_insufficient_mana" : "";
        }

        private static bool OnlyMoney(object p)
        { return UInt(p, "spirit_output") == 0 && UInt(p, "enchant_output") == 0 && UInt(p, "mana_output") == 0 && UInt(p, "exp_output") == 0 && UInt(p, "stone_output") == 0; }
        private static bool SameRecipe(object p, object template)
        {
            if (p == null || template == null) return false;
            foreach (string field in new[] { "id", "idbuilding", "time", "money_output", "mana_consume", "gold_consume", "wood_consume", "mine_consume", "demand_type", "demand_consume", "population", "prebuilding" })
                if (UInt(p, field) != UInt(template, field)) return false;
            return OnlyMoney(p) && OnlyMoney(template);
        }

        private static Type CloseType()
        {
            FieldInfo field = Model.FirstType("__BaseUI").GetField("OnClose", D.Instance);
            if (field == null || !typeof(Delegate).IsAssignableFrom(field.FieldType)) throw new ProbeFailure("shop_close_delegate_schema");
            return field.FieldType;
        }

        private static FieldInfo ProductSelector(Component panel, object product)
        {
            // The stock field is _building_product while m_products holds its
            // BuildingProduct subclass. This write is limited to that exact
            // normal product-row selector; business state stays stock-owned.
            FieldInfo field = panel.GetType().GetField("building_product", D.Instance);
            if (field == null || field.FieldType.FullName != "Database._building_product" || field.FieldType.Assembly.GetName().Name != "dbdata" ||
                product == null || product.GetType() != Model.FirstType("Database.BuildingProduct") || !field.FieldType.IsInstanceOfType(product))
                throw new ProbeFailure("shop_product_selector_schema");
            return field;
        }

        private static void SelectProduct(Component panel, object product, FieldInfo field)
        {
            field.SetValue(panel, product);
            if (!ReferenceEquals(field.GetValue(panel), product)) throw new ProbeFailure("shop_product_selector_unverified");
        }

        private string HarvestGate(Frame f)
        {
            if (harvestWindow == null || !harvestWindow.Same(f.Harvest, this) || !Ready(f.Harvest) || !ReferenceEquals(f.Sub, f.Harvest) ||
                !ReferenceEquals(D.Read(f.Harvest, "building"), target.Building) || !Hidden(f.Product) || !Hidden(f.Reward) ||
                Flag(f.Harvest, "m_bAcceling") || !HarvestStaticsIdle()) return "shop_harvest_panel_unavailable";
            return ActionGate(f, false, f.Harvest);
        }

        private string ProductGate(Frame f)
        {
            if (productWindow == null || !productWindow.Same(f.Product, this) || !Ready(f.Product) || !ReferenceEquals(f.Sub, f.Product) ||
                !ReferenceEquals(D.Read(f.Product, "building"), target.Building) || !Flag(f.Product, "m_bSub") || D.Read(f.Product, "OnClose") != null ||
                Flag(f.Product, "send") || !Hidden(f.Harvest) || !Hidden(f.Reward) || !HarvestStaticsIdle()) return "shop_product_panel_unavailable";
            return ActionGate(f, false, f.Product);
        }

        private string ProductCleanupGate(Frame f, bool captureWindow)
        {
            // MyClose is a pure normal close only for this owned null-callback
            // product panel. Never settle a reward or send a failed mutation.
            if (!hold || !Owner(f) || target == null || !ReferenceEquals(productOpeningTarget, target) ||
                productOpeningSequence <= 0 || productOpeningSequence > sequence || ownedReward != null ||
                (phase != "product_loading" && phase != "product_ready") || !TargetCurrent(f, target) || !Ready(f.Product) ||
                (DateTime)D.Read(f.Product, "visible_time") <= previousProductShown)
                return "shop_failed_product_ownership_unverified";
            if (watch != null && (!watch.CanReleaseUninvoked || watch.Receipt.Opcode != ProduceOp ||
                watch.Receipt.Operation != operationId || watch.Receipt.Sequence != sequence || !ReferenceEquals(watch.Target, target) ||
                !SameAccount(watch.Before, f) || !ReferenceEquals(watch.Before.Root, f.Root) || !ReferenceEquals(watch.Before.Product, f.Product)))
                return "shop_failed_product_request_unverified";
            if (productWindow == null && captureWindow) productWindow = new Window(f.Product, this);
            string availability = ProductGate(f);
            if (availability == "PASS" && watch != null)
            {
                if (!captureWindow) return "shop_failed_product_request_unverified";
                // No stock call or handler installation occurred. Release only
                // this unused observer after proving the normal close is owned.
                watch = null;
            }
            return availability;
        }

        private string RewardGate(Frame f)
        {
            if (!OwnReward(f) || !Ready(ownedReward) || rewardWindow == null || !rewardWindow.Same(ownedReward, this) ||
                !ReferenceEquals(f.Sub, f.Harvest) || !Hidden(f.Product) || !HarvestStaticsIdle()) return "shop_reward_not_ready";
            return ActionGate(f, true, f.Harvest, ownedReward);
        }

        private bool OwnReward(Frame f)
        {
            if (ownedReward == null || target == null || target.HarvestReceipt == null || !target.HarvestReceipt.Success ||
                !ReferenceEquals(f.Reward, ownedReward) || !ReferenceEquals(D.Read(ownedReward, "m_vctBonuses"), rewardBonuses) ||
                !ReferenceEquals(D.Read(ownedReward, "OnClose"), rewardClose) || rewardClose == null || rewardClose.GetInvocationList().Length != 1 ||
                !ReferenceEquals(rewardClose.Target, f.Harvest) || rewardClose.Method != D.Method(f.Harvest.GetType(), "HarvestOK", Type.EmptyTypes, false, typeof(void)) ||
                !ReferenceEquals(D.Read(f.Harvest, "building"), target.Building) || harvestWindow == null || !harvestWindow.Same(f.Harvest, this) ||
                D.Read(ownedReward, "m_prevUI") != null || (string)D.Read(ownedReward, "mine_ui") != "" || (string)D.Read(ownedReward, "mine_msg") != "" ||
                Flag(ownedReward, "autoClose") || Flag(ownedReward, "m_bCampaign") || D.Read(ownedReward, "m_clit") != null) return false;
            return RewardMoney(rewardBonuses) == target.HarvestReceipt.Money;
        }

        private static long RewardMoney(object value)
        {
            IList bonuses = List(value, 8);
            if (bonuses.Count != 1) throw new ProbeFailure("shop_reward_contents_unverified");
            object bonus = bonuses[0];
            IList parameters = List(D.Read(bonus, "param"), 8);
            if (UInt(bonus, "type") != 4 || parameters.Count < 2 || D.Int(parameters[0]) != 1 ||
                D.Int(parameters[1]) <= 0 || !Flag(bonus, "bAdd") || Flag(bonus, "add_to_treasury")) throw new ProbeFailure("shop_reward_contents_unverified");
            return D.Int(parameters[1]);
        }

        private string StartGate(Frame f)
        { return f.Clock != "" ? f.Clock : f.Context.Gate != "PASS" ? f.Context.Gate : FreeGate(f); }
        private string FreeGate(Frame f)
        {
            if ((f.Scene != "Capital" && f.Scene != "Campaign") || f.Sub != null || !Hidden(f.Harvest) || !Hidden(f.Product) || !Hidden(f.Reward) || !HarvestStaticsIdle())
                return "shop_unrelated_ui_or_harvest_pending";
            return ActionGate(f, false);
        }
        private string ActionGate(Frame f, bool reward, params Component[] allowed)
        {
            string panels = reward ? f.Context.RewardPanels(allowed) : f.Context.Panels(allowed);
            if (panels != "PASS") return panels;
            return Idle(f.Client) ? "PASS" : "shop_network_busy";
        }
        private static bool HarvestStaticsIdle()
        {
            Type type = Model.GameType("BuildingHarvestUI");
            return !D.Bool(D.Static(type, "_bHarvesting")) && D.UInt(D.Static(type, "_idbuilding")) == 0 && D.Static(type, "_onClose") == null;
        }
        private static bool Idle(object client)
        {
            if (List(D.Read(client, "messages"), 64).Count != 0 || List(D.Read(client, "m_sendDatas"), 64).Count != 0) return false;
            foreach (DictionaryEntry entry in Map(D.Read(client, "m_handlers"), 512))
                if (Convert.ToSingle(D.Read(entry.Value, "life"), CultureInfo.InvariantCulture) > 0f) return false;
            return true;
        }

        private Frame Read(bool blocked)
        {
            D context = DevelopmentContextReader.DailyWood(model, blocked);
            object character = context.Character;
            if (context.OwnershipGate() != "PASS") throw new ProbeFailure(context.OwnershipGate());
            Frame f = new Frame { Context = context, Character = character, Client = context.Client, Root = context.Root,
                Role = Convert.ToUInt64(D.Read(character, "id"), CultureInfo.InvariantCulture), Scene = (string)D.Read(context.Root, "LevelID"),
                Gold = Int(character, "gold"), Bound = Int(character, "bind_gold"), Mana = Int(character, "mana"), Money = D.Money(character),
                GoldConsumed = UInt(character, "gold_consume"), Sub = context.SubUi, Transport = DailyTransport.Read(context.Client) };
            uint day;
            f.Clock = DailyRefreshGate.ReadClock(character, out f.Now, out day, out f.Cycle);
            if (f.Transport == null || f.Role == 0) throw new ProbeFailure("shop_transport_or_role_unavailable");
            if (f.Scene == "Capital")
            {
                f.Capital = SceneUi(context, "CapitalUI");
                f.Harvest = SceneUi(context, "BuildingHarvestUI"); f.Product = SceneUi(context, "BuildingProductUI");
                if (f.Capital.gameObject != f.Harvest.gameObject || f.Capital.gameObject != f.Product.gameObject)
                    throw new ProbeFailure("shop_capital_panel_host_mismatch");
            }
            f.Reward = D.Static(Model.FirstType("__BaseUI"), "battle_result_ui") as Component;
            if (f.Reward != null && f.Reward.GetType() != Model.FirstType("BattleResultUI")) throw new ProbeFailure("shop_reward_cache_type");
            return f;
        }

        private static Component SceneUi(D context, string name)
        {
            Type type = Model.GameType(name); Component found = null; int count = 0;
            foreach (UnityEngine.Object item in context.All)
            {
                Component candidate = item as Component;
                if (candidate == null || candidate.GetType() != type || candidate.transform.root != context.Root.transform || !ReferenceEquals(D.Read(candidate, "rmg"), context.Root)) continue;
                found = candidate; count++;
            }
            if (count != 1) throw new ProbeFailure("shop_scene_ui_uniqueness:" + name);
            return found;
        }
        private static bool SameAccount(Frame before, Frame current)
        { return before != null && current != null && before.Role == current.Role && before.Cycle == current.Cycle && ReferenceEquals(before.Character, current.Character) && ReferenceEquals(before.Client, current.Client) && before.Transport.Same(current.Transport); }
        private bool Owner(Frame f)
        { return SameAccount(origin, f) && (capitalRoot == null || ReferenceEquals(f.Root, capitalRoot) && f.Scene == "Capital"); }
        private static bool Ready(Component ui)
        {
            // These stock BaseUI panels use Visible/IsEnable and BaseAsset's
            // IsLoading, not OnlineAwardsUI's private m_LoadOverTime field.
            return ui != null && D.Enabled(ui) && !D.Bool(D.Method(ui.GetType(), "IsLoading", Type.EmptyTypes, false, typeof(bool)).Invoke(ui, new object[0])) && !D.Loading(ui);
        }
        private static bool Hidden(Component ui) { return ui == null || !D.Visible(ui) && !D.Loading(ui); }
        private static uint UInt(object value, string field) { return D.UInt(D.Read(value, field)); }
        private static int Int(object value, string field) { return D.Int(D.Read(value, field)); }
        private static bool Flag(object value, string field) { return D.Bool(D.Read(value, field)); }
        private static IDictionary Map(object value, int bound)
        { IDictionary map = value as IDictionary; if (map == null || map.Count > bound) throw new ProbeFailure("shop_dictionary_schema_or_bound"); return map; }
        private static IList List(object value, int bound)
        { IList list = value as IList; if (list == null || list.Count > bound) throw new ProbeFailure("shop_list_schema_or_bound"); return list; }
        private static bool MoneyShop(uint id) { return id == 5 || id == 10 || id == 16 || id == 20; }
        private static int Duration(string action)
        { return action == "produce_short" ? 1200 : action == "produce_medium" ? 3600 : action == "produce_long" ? 43200 : 0; }
        private string ProductionAction() { return duration == 1200 ? "produce_short" : duration == 3600 ? "produce_medium" : "produce_long"; }

        private bool ProducedState(Frame f, Receipt receipt, Frame before)
        {
            return Owner(f) && TargetCurrent(f, target) && target.Quote != null &&
                UInt(target.Building, "idproduct") == receipt.Product && ReferenceEquals(D.Read(target.Building, "product"), target.Quote.Product) &&
                UInt(target.Building, "start_time") == receipt.Start && Flag(target.Building, "lucky") == receipt.Lucky &&
                f.Gold == before.Gold && f.Bound == before.Bound && f.GoldConsumed == before.GoldConsumed && f.Mana == checked(before.Mana - (int)receipt.Mana) &&
                Hidden(f.Product) && D.Read(f.Product, "building") == null && f.Sub == null && Hidden(f.Harvest) && Hidden(f.Reward);
        }

        private void Applied(Watch request, Frame after)
        {
            Receipt receipt = request.Receipt;
            if (!Owner(after) || !ReferenceEquals(target, request.Target) || !TargetCurrent(after, target)) throw new ProbeFailure("shop_reply_owner_changed");
            if (receipt.Opcode == ProduceOp)
            {
                if (!ProducedState(after, receipt, request.Before)) throw new ProbeFailure("shop_1158_stock_state_unverified");
                receipt.State = receipt.Success = true;
            }
            else
            {
                if (UInt(target.Building, "start_time") != receipt.Start || !ReferenceEquals(D.Read(target.Building, "product"), target.InitialProduct) ||
                    UInt(target.Building, "idproduct") != target.InitialProductId || after.Gold != request.Before.Gold || after.Bound != request.Before.Bound ||
                    after.GoldConsumed != request.Before.GoldConsumed || after.Mana != request.Before.Mana || D.Bool(D.Static(Model.GameType("BuildingHarvestUI"), "_bHarvesting")))
                    throw new ProbeFailure("shop_1154_stock_state_unverified");
                ownedReward = after.Reward;
                if (ownedReward == null || !ReferenceEquals(D.Read(ownedReward, "OnClose"), rewardClose) ||
                    ReferenceEquals(D.Read(ownedReward, "m_vctBonuses"), request.PreviousBonuses) || !Hidden(after.Product))
                    throw new ProbeFailure("shop_1154_reward_generation_unverified");
                rewardBonuses = D.Read(ownedReward, "m_vctBonuses"); rewardWindow = null;
                receipt.Money = RewardMoney(rewardBonuses); rewardMoneyBefore = request.Before.Money;
                receipt.State = receipt.Success = true;
            }
        }

        private sealed class Watch
        {
            private readonly DailyCityShopModel owner;
            private readonly object expectedArg, client;
            private readonly DailyTransport transport;
            private readonly int thread;
            private IDictionary handlers;
            private object slot;
            private Delegate original, wrapper;
            private FieldInfo callbackField;
            private DevelopmentNormalCallback forward;
            private int calls;
            internal readonly Frame Before;
            internal readonly Candidate Target;
            internal readonly Receipt Receipt;
            internal readonly object PreviousBonuses;
            internal string Error = "";
            internal bool InvocationAttempted;
            internal bool CanReleaseUninvoked
            { get { return !InvocationAttempted && handlers == null && slot == null && callbackField == null && original == null && wrapper == null &&
                calls == 0 && !Receipt.Observed && !Receipt.Returned && Receipt.Callback == "" && Error == ""; } }
            internal Watch(DailyCityShopModel owner, Frame before, Candidate target, Receipt receipt, object arg)
            {
                this.owner = owner; Before = before; Target = target; Receipt = receipt; expectedArg = arg;
                client = before.Client; transport = before.Transport; thread = Thread.CurrentThread.ManagedThreadId;
                PreviousBonuses = before.Reward == null ? null : D.Read(before.Reward, "m_vctBonuses");
            }
            internal void Attach(Component receiver, string name, bool isStatic)
            {
                handlers = Map(D.Read(client, "m_handlers"), 512); slot = handlers[(ushort)Receipt.Opcode];
                if (slot == null || slot.GetType() != Model.FirstType("game.net.tcp_base+Handler")) throw new ProbeFailure("shop_stock_handler_missing");
                original = D.Read(slot, "handler") as Delegate;
                MethodInfo method = D.Method(receiver.GetType(), name, ReplyArgs, isStatic, typeof(void));
                if (original == null || original.GetInvocationList().Length != 1 || original.Method != method ||
                    !ReferenceEquals(original.Target, isStatic ? null : receiver) || !ReferenceEquals(D.Read(slot, "arg"), expectedArg) ||
                    Convert.ToSingle(D.Read(slot, "life"), CultureInfo.InvariantCulture) != 10f) throw new ProbeFailure("shop_stock_handler_identity");
                callbackField = slot.GetType().GetField("handler", D.Instance);
                if (callbackField == null) throw new ProbeFailure("shop_stock_handler_schema");
                forward = (DevelopmentNormalCallback)(isStatic ? Delegate.CreateDelegate(typeof(DevelopmentNormalCallback), method) : Delegate.CreateDelegate(typeof(DevelopmentNormalCallback), receiver, method));
                wrapper = Delegate.CreateDelegate(original.GetType(), this, typeof(Watch).GetMethod("Dispatch", D.Instance));
                Receipt.Callback = receiver.GetType().FullName + "." + name;
                callbackField.SetValue(slot, wrapper);
            }
            private void Dispatch(MemoryStream stream, object arg)
            {
                if (Interlocked.Increment(ref calls) != 1) { Fail("shop_duplicate_stock_callback"); return; }
                bool locked = false;
                try
                {
                    locked = Monitor.TryEnter(client, 0);
                    try
                    {
                        Frame boundary = owner.Read(false);
                        if (!locked || Thread.CurrentThread.ManagedThreadId != thread || !transport.StillCurrent() || !owner.Owner(boundary) ||
                            !ReferenceEquals(owner.watch, this) || owner.sequence != Receipt.Sequence || !ReferenceEquals(owner.target, Target) ||
                            !owner.TargetCurrent(boundary, Target) || boundary.Clock != "" ||
                            !ReferenceEquals(arg, expectedArg) || !ReferenceEquals(D.Read(slot, "arg"), expectedArg) ||
                            !ReferenceEquals(D.Read(client, "m_handlers"), handlers) || handlers[(ushort)Receipt.Opcode] != null ||
                            !ReferenceEquals(callbackField.GetValue(slot), wrapper)) throw new ProbeFailure("shop_callback_boundary_changed");
                        if (stream == null || stream.GetType() != typeof(MemoryStream) || !stream.CanRead || stream.Position != 0 || stream.Length < 1 || stream.Length > 65536)
                            throw new ProbeFailure("shop_callback_stream_or_timeout");
                        byte[] body = stream.ToArray(); Decode(body, Receipt);
                        Receipt.Hash = NativeBattleStartStore.EvidenceHash(body);
                        if (Receipt.Result != 0 || Receipt.ResponseBuilding != Target.Id || Receipt.ResponseProduct != Receipt.Product ||
                            Receipt.Start < Before.Now || Receipt.Start > (ulong)D.UInt(D.Static(Model.FirstType("TimeUtil"), "_now")) + 5UL ||
                            Receipt.Immediate || Receipt.Gold != 0 || Receipt.Opcode == ProduceOp && Receipt.Mana != Target.Quote.Mana ||
                            Receipt.Opcode == HarvestOp && Receipt.Lucky != Flag(Target.Building, "lucky")) throw new ProbeFailure("shop_reply_target_or_cost_unverified");
                    }
                    catch (Exception error) { Fail(error is ProbeFailure ? error.Message : "shop_reply_schema_unverified"); }
                    // Preserve this exact stock registration's normal lifecycle,
                    // including a null timeout. Observation never fabricates a
                    // response, consumes its stream or installs account state.
                    try { forward(stream, arg); Receipt.Returned = true; }
                    catch { Fail("shop_original_callback_threw"); throw; }
                    if (Error == "")
                    {
                        try { owner.Applied(this, owner.Read(false)); }
                        catch (Exception error) { Fail(error is ProbeFailure ? error.Message : "shop_stock_state_unverified"); }
                    }
                    owner.Publish();
                }
                finally { if (locked) Monitor.Exit(client); }
            }
            private void Fail(string detail) { Error = Receipt.Error = detail; owner.Unknown(detail); }
            internal bool Restore()
            {
                if (callbackField == null) return false;
                if (!Monitor.TryEnter(client, 0)) return false;
                try
                {
                    if (!ReferenceEquals(callbackField.GetValue(slot), wrapper)) return ReferenceEquals(callbackField.GetValue(slot), original);
                    callbackField.SetValue(slot, original); return ReferenceEquals(callbackField.GetValue(slot), original);
                }
                finally { Monitor.Exit(client); }
            }
        }

        // Detached protobuf scalar observation for the two pinned stock reply
        // contracts. Optional fields retain their declared defaults.
        private static void Decode(byte[] body, Receipt receipt)
        {
            uint[] values = new uint[9]; int seen = 0, position = 0;
            while (position < body.Length)
            {
                ulong tag = Varint(body, ref position); int field = checked((int)(tag >> 3)), wire = (int)(tag & 7);
                if (field < 1) throw new FormatException();
                int maximum = receipt.Opcode == ProduceOp ? 8 : 5;
                if (field <= maximum)
                {
                    if (wire != 0 || (seen & (1 << field)) != 0) throw new FormatException();
                    ulong number = Varint(body, ref position); if (number > UInt32.MaxValue) throw new FormatException();
                    values[field] = (uint)number; seen |= 1 << field;
                }
                else if (wire == 0) Varint(body, ref position);
                else if (wire == 1) position = checked(position + 8);
                else if (wire == 2) { int count = checked((int)Varint(body, ref position)); position = checked(position + count); }
                else if (wire == 5) position = checked(position + 4);
                else throw new FormatException();
                if (position > body.Length) throw new FormatException();
            }
            if ((seen & 2) == 0 || values[5] > 1 || values[8] > 1) throw new FormatException();
            receipt.Observed = true; receipt.Result = values[1]; receipt.ResponseBuilding = values[2]; receipt.ResponseProduct = values[3];
            receipt.Start = values[4]; receipt.Lucky = values[5] != 0; receipt.Gold = values[6]; receipt.Mana = values[7]; receipt.Immediate = values[8] != 0;
        }
        private static ulong Varint(byte[] body, ref int position)
        {
            ulong value = 0;
            for (int shift = 0; shift < 70; shift += 7)
            {
                if (position >= body.Length) throw new FormatException();
                byte b = body[position++]; if (shift == 63 && b > 1) throw new FormatException();
                value |= (ulong)(b & 127) << shift; if ((b & 128) == 0) return value;
            }
            throw new FormatException();
        }

        internal void Stop(string detail)
        {
            stopped = true; queued = null;
            // The pending stock callback may still settle its own request. It
            // remains observed, but no new send or close action is offered.
            status = attempted || hold ? "UNKNOWN" : "STOPPED"; reason = gate = detail; Publish();
        }
        internal void NotInvoked(string detail)
        {
            queued = null; notInvokedSequence = sequence; GuiInvocationAttempted = false;
            if (hold || attempted) Unknown(detail);
            else { status = "BLOCKED"; reason = gate = detail; terminal = true; }
            Publish();
        }
        private void Unknown(string detail)
        {
            if (failureReason == "") failureReason = detail;
            status = "UNKNOWN"; reason = gate = failureReason;
            cleanupReason = detail; cleanupAction = ""; cleanupVerified = false;
        }
        private int CollectedCount() { int count = 0; foreach (Candidate c in candidates) if (c.Collected) count++; return count; }
        private int ProducedCount() { int count = 0; foreach (Candidate c in candidates) if (c.Produced) count++; return count; }
        private void Publish()
        {
            string value = Json(); value = value.Substring(value.IndexOf(",\"task_id\"", StringComparison.Ordinal));
            if (stamp != value) { stamp = value; Revision++; }
        }
        internal string Json()
        {
            long mana = 0, gold = 0, money = 0; bool returned = false;
            StringBuilder receiptJson = new StringBuilder("[");
            foreach (Receipt receipt in receipts)
            {
                if (receiptJson.Length > 1) receiptJson.Append(','); receiptJson.Append(receipt.Json());
                if (receipt.Observed) { gold += receipt.Gold; mana += receipt.Mana; }
                if (receipt.MoneyCredited) money += receipt.Money;
                returned |= receipt.Returned;
            }
            receiptJson.Append(']');
            StringBuilder skipJson = new StringBuilder("[");
            foreach (Skip skip in skips) { if (skipJson.Length > 1) skipJson.Append(','); skipJson.Append(skip.Json()); }
            skipJson.Append(']');
            bool clean = cleanupVerified && terminal && !hold && watch == null;
            return "{\"schema\":1,\"revision\":" + Revision + ",\"task_id\":308,\"role_id\":" + D.Q(frame == null ? "0" : frame.Role.ToString(CultureInfo.InvariantCulture)) +
                ",\"cycle\":" + (frame == null ? 0 : frame.Cycle) + ",\"status\":" + D.Q(queued == null ? status : "QUEUED") + ",\"phase\":" + D.Q(phase) +
                ",\"reason\":" + D.Q(reason) + ",\"gate\":" + D.Q(gate) + ",\"next_action\":" + D.Q(Offer()) + ",\"operation_id\":" + D.Q(operationId) +
                ",\"sequence\":" + sequence + ",\"gui_invocation_attempted\":" + D.B(GuiInvocationAttempted) + ",\"gui_not_invoked_sequence\":" + notInvokedSequence +
                ",\"observation_available\":" + D.B(observationAvailable) + ",\"duration_seconds\":" + duration + ",\"candidate_count\":" + candidates.Count +
                ",\"processed_candidates\":" + cursor + ",\"collected_buildings\":" + CollectedCount() + ",\"produced_buildings\":" + ProducedCount() + ",\"skipped_buildings\":" + skips.Count +
                ",\"target_id\":" + (target == null ? 0 : target.Id) + ",\"target_template_id\":" + (target == null ? 0 : target.Template) +
                ",\"selected_product_id\":" + (target == null || target.Quote == null ? 0 : target.Quote.Id) +
                ",\"quoted_mana_cost\":" + (target == null || target.Quote == null ? 0 : target.Quote.Mana) +
                ",\"quoted_money_output\":" + (target == null || target.Quote == null ? 0 : target.Quote.Money) +
                ",\"gold_cost\":" + gold + ",\"mana_cost\":" + mana + ",\"money_received\":" + money +
                ",\"mutation_attempted\":" + D.B(attempted) + ",\"stock_callback_returned\":" + D.B(returned) +
                ",\"cleanup_pending\":" + D.B(hold || !observationAvailable && operationId != "" && GuiInvocationAttempted) + ",\"busy\":" + D.B(Busy) + ",\"home_ready\":" + D.B(clean && observationAvailable) +
                ",\"cleanup_reason\":" + D.Q(cleanupReason) + ",\"failure_reason\":" + D.Q(failureReason) + ",\"product_open_sequence\":" + productOpeningSequence +
                ",\"product_visible_ticks\":" + (productWindow == null ? 0L : productWindow.Shown.Ticks) +
                ",\"reward_pending\":" + D.B(ownedReward != null) + ",\"limited\":" + D.B(limited) +
                ",\"task_confirmed\":" + D.B(clean && status == "COMPLETED") + ",\"checklist_complete\":" + D.B(clean && (status == "COMPLETED" || status == "NO_FREE_REWARD")) +
                ",\"receipts\":" + receiptJson + ",\"skipped\":" + skipJson + "}";
        }
    }
}
