using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal delegate bool BattleLaunchCheck();
    internal delegate void BattleLaunchAction();

    // This is admission to a native battle Send, not settlement or a result.
    // Python sets the per-epoch event, then drains this exact named mutex before
    // acknowledging pause/stop. Neither endpoint resets a cancelled event.
    internal static class BattleLaunch
    {
        internal const string Interface = "named-event-launch-fence-v1";
        private static string nonce;
        private static BattleLaunchCheck lease;
        private static bool stopped;
        [ThreadStatic] private static Command command;
        private static readonly ArrayList attempts = new ArrayList();
        private static BattleLaunchAttempt last;

        internal static void Initialize(string sessionNonce, BattleLaunchCheck validLease)
        {
            if (!Command.ValidNonce(sessionNonce) || validLease == null) throw new ProbeFailure("battle_launch_session_identity");
            nonce = sessionNonce; lease = validLease; stopped = false; command = null;
        }
        internal static void Select(Command value) { command = value; }
        internal static bool Lease() { return !stopped && lease != null && lease(); }

        private static BattleLaunchAttempt New(BattleMode mode, object role, object client,
            Component source, object target, BattleLaunchCheck verify)
        {
            if (command == null || !Command.ValidNonce(command.BattleEpoch) || !Lease())
                throw new ProbeFailure("battle_launch_epoch_or_lease_missing");
            for (int index = attempts.Count - 1; index >= 0; index--)
            {
                BattleLaunchAttempt prior = (BattleLaunchAttempt)attempts[index];
                if (prior.Unresolved) throw new ProbeFailure("battle_launch_prior_pending_or_unknown");
                prior.Retire(); attempts.RemoveAt(index);
            }
            BattleLaunchAttempt value = new BattleLaunchAttempt(nonce, command.BattleEpoch, command.Sequence,
                command.Name == "campaign-command" || command.Name == "challenge-command" ? command.AttemptId : command.OperationId,
                mode, role, client, source, target, verify, lease);
            attempts.Add(value); last = value;
            return value;
        }

        internal static BattleLaunchAttempt Formation(BattleMode mode, Component formation, Component source,
            object target, BattleLaunchCheck verify, BattleLaunchAction afterAttack)
        {
            object role = D.Static(Model.FirstType("Global"), "character_");
            object client = DailyTransport.CurrentClient(role);
            BattleLaunchAttempt value = New(mode, role, client, source, target, verify);
            value.StartFormation(formation, afterAttack);
            return value;
        }
        internal static BattleLaunchAttempt Direct(BattleMode mode, Component source, object target,
            BattleLaunchCheck verify, BattleLaunchAction invoke, ushort opcode)
        {
            object role = D.Static(Model.FirstType("Global"), "character_");
            BattleLaunchAttempt value = New(mode, role, DailyTransport.CurrentClient(role), source, target, verify);
            value.StartDirect(invoke, opcode);
            return value;
        }
        internal static BattleLaunchAttempt NonReport(BattleMode mode, Component source, object target,
            BattleLaunchCheck verify, BattleLaunchAction invoke, ushort opcode)
        {
            object role = D.Static(Model.FirstType("Global"), "character_");
            BattleLaunchAttempt value = New(mode, role, DailyTransport.CurrentClient(role), source, target, verify);
            value.StartNonReport(invoke, opcode);
            return value;
        }
        internal static void ReportBound(BattlePresentationOwnership ownership, object role, ulong roleId, Component battle, object report)
        {
            if (last == null) return; // An already-owned manual report has no automated launch to transfer.
            if (!last.Transfer(ownership, role, roleId, battle, report))
                throw new ProbeFailure("battle_launch_report_transfer_unverified");
        }
        internal static bool OwnsAttack(object formation, object original)
        {
            foreach (BattleLaunchAttempt value in attempts)
                if (value.OwnsAttack(formation, original)) return true;
            return false;
        }
        internal static bool OwnsScenario(Component ui, string attemptId, object role, Component source, object target)
        { return last != null && last.OwnsScenario(ui, attemptId, role, source, target); }
        internal static string Error
        {
            get
            {
                foreach (BattleLaunchAttempt value in attempts)
                    if (value.Error != "") return value.Error;
                return "";
            }
        }
        internal static void Validate()
        { foreach (BattleLaunchAttempt value in attempts) value.Validate(); }
        internal static bool Stop()
        {
            stopped = true;
            bool clean = true;
            foreach (BattleLaunchAttempt value in attempts)
            { value.CancelLocally(); if (value.Unresolved) clean = false; }
            return clean;
        }
        internal static string Json() { return last == null ? "null" : last.Json(); }
    }

    internal sealed class BattleLaunchAttempt
    {
        private const uint Sync = 0x00100000, ModifyMutex = 1;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenMutexW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenEventW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern bool ReleaseMutex(IntPtr handle);
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern bool CloseHandle(IntPtr handle);
        private readonly string epoch, operationId;
        private readonly int sequence, thread, tick;
        private readonly BattleMode mode;
        private readonly object role, client, target, loader, root;
        private readonly ulong roleId;
        private readonly Component source;
        private readonly DailyTransport transport;
        private readonly BattleLaunchCheck verify, lease;
        private readonly IDictionary handlers;
        private readonly Component beforeBattle;
        private readonly object beforeReport;
        private IntPtr mutex, cancelled;
        private Component formation, scenario, staminaMain;
        private object form, parameters, scenarioData, responseHandler, responseArgument;
        private FieldInfo attackField, replyField, scenarioField, staminaField;
        private Delegate attackOriginal, attackWrapper, replyOriginal, replyWrapper, scenarioOriginal, scenarioWrapper, staminaOriginal, staminaWrapper;
        private BattleLaunchCheck attackForward, staminaForward;
        private DevelopmentNormalCallback replyForward;
        private BattleLaunchAction scenarioForward, afterAttack;
        private object requestHandler, requestArgument, initialReport;
        private Component initialBattle;
        private FieldInfo requestField;
        private Delegate requestWrapper;
        private DevelopmentNormalCallback requestForward;
        private ushort requestOpcode;
        private uint nonReportResult;
        private int requestCalls;
        private int formationCalls, attackCalls, continuationCalls;
        private bool locallyCancelled, formationReturned, scenarioPending, staminaPending, cancelledBeforeBattle, reportTransferred, nonReportOwned, isNonReport;
        internal bool FormationUpdateAttempted { get; private set; }
        internal bool BattleLaunchInvoked { get; private set; }
        internal bool BattleRequestObserved { get; private set; }
        internal string Error { get; private set; }
        internal bool Unresolved { get { return Error != "" || !reportTransferred && !cancelledBeforeBattle && !nonReportOwned; } }
        internal bool NonReportReturned { get; private set; }

        internal BattleLaunchAttempt(string nonce, string cancelEpoch, int commandSequence, string operation, BattleMode battleMode,
            object character, object tcpClient, Component nativeSource, object nativeTarget,
            BattleLaunchCheck identity, BattleLaunchCheck validLease)
        {
            epoch = cancelEpoch; sequence = commandSequence; operationId = operation; mode = battleMode;
            role = character; client = tcpClient; source = nativeSource; target = nativeTarget;
            verify = identity; lease = validLease; thread = Thread.CurrentThread.ManagedThreadId; tick = Environment.TickCount;
            Error = "";
            roleId = role == null ? 0 : Convert.ToUInt64(D.Read(role, "id"), CultureInfo.InvariantCulture);
            transport = DailyTransport.Read(client);
            loader = D.Static(Model.FirstType("BaseAsset"), "lds");
            root = loader == null ? null : D.Read(loader, "currmg");
            handlers = client == null ? null : D.Read(client, "m_handlers") as IDictionary;
            GameObject oldBattle = GameObject.Find("Battle/BattleUI");
            beforeBattle = oldBattle == null ? null : oldBattle.GetComponent(Model.GameType("BattleUI"));
            object oldInterpreter = beforeBattle == null ? null : D.Read(beforeBattle, "interpreter");
            beforeReport = oldInterpreter == null ? null : D.Read(oldInterpreter, "m_report");
            mutex = OpenMutexW(Sync | ModifyMutex, false, "Local\\JSAssistantBattleLaunch-" + nonce);
            cancelled = OpenEventW(Sync, false, "Local\\JSAssistantBattleCancel-" + nonce + "-" + epoch);
            if (mutex == IntPtr.Zero || cancelled == IntPtr.Zero || transport == null || roleId == 0 ||
                handlers == null || source == null || target == null || verify == null || lease == null || !Command.ValidNonce(operationId))
            { Unknown("battle_launch_handles_or_identity_unavailable"); throw new ProbeFailure(Error); }
        }
        ~BattleLaunchAttempt()
        { if (mutex != IntPtr.Zero) CloseHandle(mutex); if (cancelled != IntPtr.Zero) CloseHandle(cancelled); }
        internal void Retire()
        {
            if (Unresolved) return;
            // No pending launch continuation remains. Old saved delegates are
            // still one-shot, and cannot reuse this token after a new attempt.
            locallyCancelled = true;
            if (mutex != IntPtr.Zero) { CloseHandle(mutex); mutex = IntPtr.Zero; }
            if (cancelled != IntPtr.Zero) { CloseHandle(cancelled); cancelled = IntPtr.Zero; }
        }

        private void Unknown(string why) { if (Error == "") Error = why; }
        private bool Cancelled()
        {
            if (locallyCancelled) return true;
            uint state = WaitForSingleObject(cancelled, 0);
            if (state == 0) return true;
            if (state != 258) Unknown("battle_launch_cancel_event_unverifiable");
            return state != 258;
        }
        private bool RoleAndTransport()
        {
            return Thread.CurrentThread.ManagedThreadId == thread && role != null && source != null &&
                ReferenceEquals(D.Static(Model.FirstType("Global"), "character_"), role) &&
                Convert.ToUInt64(D.Read(role, "id"), CultureInfo.InvariantCulture) == roleId &&
                ReferenceEquals(DailyTransport.CurrentClient(role), client) && transport.StillCurrent() &&
                ReferenceEquals(D.Static(Model.FirstType("BaseAsset"), "lds"), loader);
        }
        private bool Identity() { return RoleAndTransport() && ReferenceEquals(D.Read(loader, "currmg"), root) && verify(); }
        private bool Enter()
        {
            if (Error != "") return false;
            uint result = WaitForSingleObject(mutex, 0);
            if (result == 0x80) { ReleaseMutex(mutex); Unknown("battle_launch_mutex_abandoned"); return false; }
            if (result != 0) { Unknown("battle_launch_mutex_unavailable"); return false; }
            try
            {
                if (Cancelled()) { ReleaseMutex(mutex); return false; }
                if (!lease() || !Identity())
                { Unknown("battle_launch_identity_or_lease_changed"); ReleaseMutex(mutex); return false; }
                return true;
            }
            catch (Exception) { Unknown("battle_launch_identity_unverifiable"); ReleaseMutex(mutex); return false; }
        }
        private void Exit() { if (!ReleaseMutex(mutex)) Unknown("battle_launch_mutex_release_unverified"); }

        internal void StartFormation(Component ui, BattleLaunchAction after)
        {
            formation = ui; afterAttack = after;
            if (!Enter()) throw new ProbeFailure(Error == "" ? "battle_launch_cancelled_before_formation" : Error);
            bool locked = false;
            try
            {
                locked = Monitor.TryEnter(client, 0);
                if (!locked || formation == null || formation.GetType() != Model.GameType("FormationUI") || handlers[(ushort)298] != null)
                    throw new ProbeFailure("battle_launch_formation_or_handler_busy");
                form = D.Read(formation, "CharFormation"); parameters = D.Read(formation, "m_lastparam");
                attackField = Field(formation, "OnAttack");
                attackOriginal = attackField.GetValue(formation) as Delegate;
                IDictionary args = parameters as IDictionary;
                if (form == null || args == null || attackOriginal == null || attackOriginal.GetInvocationList().Length != 1 ||
                    attackOriginal.Method.ReturnType != typeof(bool) || attackOriginal.Method.GetParameters().Length != 0 ||
                    !ReferenceEquals(args["OnAttack"], attackOriginal)) throw new ProbeFailure("battle_launch_attack_contract");
                if (handlers[Opcode()] != null) throw new ProbeFailure("battle_launch_battle_handler_already_registered");
                attackForward = (BattleLaunchCheck)Delegate.CreateDelegate(typeof(BattleLaunchCheck), attackOriginal.Target, attackOriginal.Method);
                attackWrapper = Delegate.CreateDelegate(attackOriginal.GetType(), this, GetType().GetMethod("Attack", D.Instance));
                attackField.SetValue(formation, attackWrapper); args["OnAttack"] = attackWrapper;
                D.Selection(formation, "onfight", true);
                FormationUpdateAttempted = true;
                D.Call(formation, "OnClickSetCurrentForm");
                // Divinity keeps its existing exact 298 receipt observer. It
                // must bind synchronously here, before another Run dispatch.
                if (mode != BattleMode.Divinity) AttachFormationReply();
            }
            catch (Exception) { Unknown("battle_launch_formation_invocation_unknown"); throw; }
            finally { if (locked) Monitor.Exit(client); Exit(); }
        }
        private void AttachFormationReply()
        {
            responseHandler = handlers[(ushort)298];
            if (responseHandler == null) throw new ProbeFailure("battle_launch_298_handler_missing");
            replyField = Field(responseHandler, "handler"); replyOriginal = replyField.GetValue(responseHandler) as Delegate;
            responseArgument = D.Read(responseHandler, "arg");
            if (replyOriginal == null || replyOriginal.GetInvocationList().Length != 1 || !ReferenceEquals(replyOriginal.Target, formation) ||
                replyOriginal.Method.Name != "sc_formupdate") throw new ProbeFailure("battle_launch_298_handler_owner");
            replyForward = (DevelopmentNormalCallback)Delegate.CreateDelegate(typeof(DevelopmentNormalCallback), replyOriginal.Target, replyOriginal.Method);
            replyWrapper = Delegate.CreateDelegate(replyOriginal.GetType(), this, GetType().GetMethod("FormationReply", D.Instance));
            replyField.SetValue(responseHandler, replyWrapper);
        }
        private void FormationReply(MemoryStream stream, object arg)
        {
            if (Interlocked.Increment(ref formationCalls) != 1) { Unknown("battle_launch_298_duplicate"); return; }
            bool locked = Monitor.TryEnter(client, 0);
            try
            {
                if (!locked || !Identity() || !SameFormation() || !ReferenceEquals(arg, responseArgument) ||
                    !ReferenceEquals(replyField.GetValue(responseHandler), replyWrapper) ||
                    handlers[(ushort)298] != null && !ReferenceEquals(handlers[(ushort)298], responseHandler))
                { Unknown("battle_launch_298_identity_changed"); return; }
                // Even a cancelled response applies its native formation update
                // exactly once. Its OnAttack is still our fenced wrapper.
                replyForward(stream, arg); formationReturned = true;
                if (stream == null) Unknown("battle_launch_298_timeout_unknown");
                if (Cancelled() && !BattleRequestObserved && !scenarioPending && !staminaPending && Error == "")
                    cancelledBeforeBattle = true;
                RestoreCompleted();
            }
            catch (Exception) { Unknown("battle_launch_298_callback_unknown"); throw; }
            finally { if (locked) Monitor.Exit(client); }
        }
        private bool SameFormation()
        {
            IDictionary args = parameters as IDictionary;
            return formation != null && ReferenceEquals(D.Read(formation, "CharFormation"), form) &&
                ReferenceEquals(D.Read(formation, "m_lastparam"), parameters) && args != null &&
                ReferenceEquals(args["OnAttack"], attackWrapper) && ReferenceEquals(attackField.GetValue(formation), attackWrapper);
        }
        internal bool OwnsAttack(object ui, object original)
        { return ReferenceEquals(formation, ui) && ReferenceEquals(attackOriginal, original) && SameFormation(); }
        internal bool OwnsScenario(Component ui, string attemptId, object character, Component nativeSource, object nativeTarget)
        {
            try
            {
                return (mode == BattleMode.Campaign || mode == BattleMode.NormalChallenge) && Error == "" &&
                    scenarioPending && !BattleRequestObserved && operationId == attemptId && Command.ValidNonce(attemptId) &&
                    ReferenceEquals(role, character) && ReferenceEquals(source, nativeSource) && ReferenceEquals(target, nativeTarget) &&
                    ReferenceEquals(scenario, ui) && scenario != null && scenarioField != null &&
                    ReferenceEquals(scenarioField.GetValue(scenario), scenarioWrapper) &&
                    ReferenceEquals(D.Read(scenario, "m_scenario"), scenarioData) && SameFormation() &&
                    lease() && !Cancelled() && Identity();
            }
            catch (Exception) { return false; }
        }
        private bool Attack()
        {
            if (Interlocked.Increment(ref attackCalls) != 1) { Unknown("battle_launch_attack_duplicate"); return false; }
            if (!Enter()) return false;
            try
            {
                if (!SameFormation()) { Unknown("battle_launch_formation_changed"); return false; }
                if (mode == BattleMode.Trial && Convert.ToInt32(D.Read(role, "stamina"), CultureInfo.InvariantCulture) < 1)
                { Unknown("battle_launch_trial_stamina_changed"); return false; }
                BattleLaunchInvoked = true;
                bool result = attackForward();
                ObserveOrAttachContinuation();
                if (BattleRequestObserved && afterAttack != null) afterAttack();
                return result;
            }
            catch (Exception) { Unknown("battle_launch_attack_invocation_unknown"); throw; }
            finally { Exit(); }
        }
        private ushort Opcode()
        {
            switch (mode)
            {
                case BattleMode.Campaign: case BattleMode.NormalChallenge: return 0x402;
                case BattleMode.Grave: return 0x342;
                case BattleMode.Trial: return 0x334;
                case BattleMode.TrialBoss: return 0x336;
                case BattleMode.TrainingMonster: return 0x368;
                case BattleMode.Divinity: return 0x4B1;
                case BattleMode.OwnBuildingMonster: return 0x48D;
                case BattleMode.FriendBuildingMonster: return 0x4C1;
                case BattleMode.OwnDemon: return 0x492;
                default: throw new ProbeFailure("battle_launch_mode_contract");
            }
        }
        private bool ObserveRequest(ushort opcode)
        {
            object handler = handlers[opcode];
            Delegate callback = handler == null ? null : D.Read(handler, "handler") as Delegate;
            if (callback == null) return false;
            if (callback.GetInvocationList().Length != 1 || !ReferenceEquals(callback.Target, source))
                throw new ProbeFailure("battle_launch_request_owner_changed");
            requestHandler = handler; requestOpcode = opcode;
            requestField = Field(handler, "handler");
            requestArgument = D.Read(handler, "arg");
            BattleRequestObserved = true;
            if (isNonReport || formation != null && mode != BattleMode.Divinity && mode != BattleMode.OwnDemon)
            {
                requestForward = (DevelopmentNormalCallback)Delegate.CreateDelegate(typeof(DevelopmentNormalCallback), callback.Target, callback.Method);
                requestWrapper = Delegate.CreateDelegate(callback.GetType(), this, GetType().GetMethod("InitialReply", D.Instance));
                requestField.SetValue(handler, requestWrapper);
            }
            return true;
        }
        private void InitialReply(MemoryStream stream, object arg)
        {
            if (Interlocked.Increment(ref requestCalls) != 1) { Unknown("battle_launch_initial_reply_duplicate"); return; }
            bool locked = Monitor.TryEnter(client, 0);
            try
            {
                // A sent request still belongs to the game after cancellation.
                // Apply only this exact original response, once, to its captured
                // role/source/transport. No admission to a new battle occurs here.
                if (!locked || !RoleAndTransport() || !ResponseSourceCurrent() || !ReferenceEquals(D.Read(loader, "currmg"), root) ||
                    !ReferenceEquals(arg, requestArgument) || !ReferenceEquals(requestField.GetValue(requestHandler), requestWrapper) ||
                    handlers[requestOpcode] != null && !ReferenceEquals(handlers[requestOpcode], requestHandler))
                { Unknown("battle_launch_initial_reply_identity_changed"); return; }
                byte[] nonReportBody = isNonReport && stream != null ? stream.ToArray() : null;
                requestForward(stream, arg);
                if (stream == null) { Unknown("battle_launch_initial_reply_timeout_unknown"); return; }
                if (isNonReport)
                {
                    uint[] values = DecodeNonReport(nonReportBody);
                    NonReportReturned = true; nonReportResult = values[1];
                    if (requestOpcode != 0x370 || values[0] == 0 ||
                        !ReferenceEquals(D.Read(role, "sealmonsterinfo"), target) ||
                        Convert.ToUInt32(D.Read(target, "buff"), CultureInfo.InvariantCulture) != values[2])
                    { Unknown("battle_launch_370_no_stock_success_no_retry"); return; }
                    for (int slot = 1; slot <= 6; slot++)
                        if (!(bool)D.Read(target, "finish" + slot))
                        { Unknown("battle_launch_370_native_progress_unverified"); return; }
                    nonReportOwned = true; return;
                }
                GameObject game = GameObject.Find("Battle/BattleUI");
                Component battle = game == null ? null : game.GetComponent(Model.GameType("BattleUI"));
                object interpreter = battle == null ? null : D.Read(battle, "interpreter");
                InitialResponseApplied(battle, interpreter == null ? null : D.Read(interpreter, "m_report"));
            }
            catch (Exception) { Unknown("battle_launch_initial_reply_unknown"); throw; }
            finally { if (locked) Monitor.Exit(client); }
        }
        private bool ResponseSourceCurrent()
        {
            if (isNonReport) return ReferenceEquals(D.Read(role, "sealmonsterinfo"), target);
            if (mode == BattleMode.Campaign || mode == BattleMode.NormalChallenge)
            {
                IDictionary troops = D.Read(source, "m_troops") as IDictionary;
                int id = Convert.ToInt32(D.Read(source, "m_troopID"), CultureInfo.InvariantCulture);
                return troops != null && ReferenceEquals(troops[(byte)id], target);
            }
            if (mode == BattleMode.TrialBoss) return ReferenceEquals(D.Read(role, "trialinfo"), target);
            if (mode == BattleMode.OwnBuildingMonster || mode == BattleMode.FriendBuildingMonster)
                return ReferenceEquals(D.Read(source, "Monster"), target);
            return ReferenceEquals(D.Read(source, "m_troop"), target);
        }
        private static uint[] DecodeNonReport(byte[] bytes)
        {
            // Pinned pbdata SCSealMonsterKillall_0x370: required uint exp=1,
            // result=2, buff=3. This copies evidence, never fabricates a report.
            if (bytes == null || bytes.Length < 6 || bytes.Length > 64) throw new ProbeFailure("battle_launch_370_wire_unverified");
            uint[] values = new uint[3]; int seen = 0, pos = 0;
            while (pos < bytes.Length)
            {
                int tag = bytes[pos++], index = (tag >> 3) - 1;
                if ((tag & 7) != 0 || index < 0 || index > 2 || (seen & (1 << index)) != 0)
                    throw new ProbeFailure("battle_launch_370_wire_unverified");
                uint value = 0; bool complete = false;
                for (int shift = 0; shift <= 28 && pos < bytes.Length; shift += 7)
                {
                    byte part = bytes[pos++];
                    if (shift == 28 && part > 15) throw new ProbeFailure("battle_launch_370_wire_unverified");
                    value |= (uint)(part & 127) << shift;
                    if (part < 128) { complete = true; break; }
                }
                if (!complete) throw new ProbeFailure("battle_launch_370_wire_unverified");
                seen |= 1 << index; values[index] = value;
            }
            if (seen != 7) throw new ProbeFailure("battle_launch_370_wire_unverified");
            return values;
        }
        internal void InitialResponseApplied(Component battle, object report)
        {
            if (!BattleRequestObserved || requestHandler == null || !RoleAndTransport() || battle == null || report == null ||
                ReferenceEquals(report, beforeReport) || handlers[requestOpcode] != null && !ReferenceEquals(handlers[requestOpcode], requestHandler))
            { Unknown("battle_launch_initial_report_unverified"); throw new ProbeFailure(Error); }
            object interpreter = D.Read(battle, "interpreter");
            if (interpreter == null || !ReferenceEquals(D.Read(interpreter, "m_report"), report) ||
                initialReport != null && (!ReferenceEquals(initialReport, report) || !ReferenceEquals(initialBattle, battle)))
            { Unknown("battle_launch_initial_report_conflict"); throw new ProbeFailure(Error); }
            initialBattle = battle; initialReport = report;
        }
        internal bool Transfer(BattlePresentationOwnership ownership, object character, ulong id, Component battle, object report)
        {
            if (reportTransferred)
                return ownership != null && ownership.Mode == mode && ownership.OperationId == operationId &&
                    ReferenceEquals(ownership.Source, source) && ReferenceEquals(ownership.Target, target) &&
                    ReferenceEquals(character, role) && id == roleId && ReferenceEquals(initialBattle, battle) && ReferenceEquals(initialReport, report);
            if (Error != "" || locallyCancelled || !lease() || !RoleAndTransport() || !BattleRequestObserved ||
                initialReport == null || ownership == null || ownership.Mode != mode || ownership.OperationId != operationId ||
                !ReferenceEquals(ownership.Source, source) || !ReferenceEquals(ownership.Target, target) ||
                !ReferenceEquals(character, role) || id != roleId || !ReferenceEquals(initialBattle, battle) || !ReferenceEquals(initialReport, report))
            { Unknown("battle_launch_initial_report_transfer_mismatch"); return false; }
            reportTransferred = true; return true;
        }
        private void ObserveOrAttachContinuation()
        {
            if (ObserveRequest(Opcode())) return;
            if (mode == BattleMode.Campaign || mode == BattleMode.NormalChallenge)
            {
                GameObject global = GameObject.Find("Root/RootUI");
                scenario = global == null ? null : global.GetComponent(Model.GameType("ScenarioUI"));
                if (scenario != null)
                {
                    scenarioField = Field(scenario, "OnClose"); scenarioOriginal = scenarioField.GetValue(scenario) as Delegate;
                    if (scenarioOriginal != null && scenarioOriginal.GetInvocationList().Length == 1 &&
                        ReferenceEquals(scenarioOriginal.Target, source) && scenarioOriginal.Method.Name == "BattleCampaignOK")
                    {
                        scenarioData = D.Read(scenario, "m_scenario");
                        scenarioForward = (BattleLaunchAction)Delegate.CreateDelegate(typeof(BattleLaunchAction), scenarioOriginal.Target, scenarioOriginal.Method);
                        scenarioWrapper = Delegate.CreateDelegate(scenarioOriginal.GetType(), this, GetType().GetMethod("Scenario", D.Instance));
                        scenarioPending = true; scenarioField.SetValue(scenario, scenarioWrapper); return;
                    }
                }
            }
            if (mode == BattleMode.Trial)
            {
                staminaMain = (root as Component).GetComponentInChildren(Model.GameType("MainUI"));
                if (staminaMain != null)
                {
                    staminaField = Field(staminaMain, "stFight"); staminaOriginal = staminaField.GetValue(staminaMain) as Delegate;
                    if (staminaOriginal != null && staminaOriginal.GetInvocationList().Length == 1 &&
                        ReferenceEquals(staminaOriginal.Target, attackOriginal.Target) && staminaOriginal.Method == attackOriginal.Method)
                    {
                        staminaForward = (BattleLaunchCheck)Delegate.CreateDelegate(typeof(BattleLaunchCheck), staminaOriginal.Target, staminaOriginal.Method);
                        staminaWrapper = Delegate.CreateDelegate(staminaOriginal.GetType(), this, GetType().GetMethod("Stamina", D.Instance));
                        staminaPending = true; staminaField.SetValue(staminaMain, staminaWrapper); return;
                    }
                }
            }
            Unknown("battle_launch_request_unobserved_no_retry");
        }
        private void Scenario()
        {
            if (Interlocked.Increment(ref continuationCalls) != 1) { Unknown("battle_launch_continuation_duplicate"); return; }
            if (!Enter()) return;
            try
            {
                if (!scenarioPending || scenario == null || !ReferenceEquals(scenarioField.GetValue(scenario), scenarioWrapper) ||
                    !ReferenceEquals(D.Read(scenario, "m_scenario"), scenarioData))
                { Unknown("battle_launch_scenario_identity_changed"); return; }
                scenarioPending = false;
                scenarioForward();
                if (!ObserveRequest(Opcode())) Unknown("battle_launch_scenario_request_unobserved");
                if (BattleRequestObserved && afterAttack != null) afterAttack();
                RestoreCompleted();
            }
            catch (Exception) { Unknown("battle_launch_scenario_invocation_unknown"); throw; }
            finally { Exit(); }
        }
        private bool Stamina()
        {
            if (Interlocked.Increment(ref continuationCalls) != 1) { Unknown("battle_launch_continuation_duplicate"); return false; }
            if (!Enter()) return false;
            try
            {
                if (!staminaPending || staminaMain == null || !ReferenceEquals(staminaField.GetValue(staminaMain), staminaWrapper) ||
                    Convert.ToInt32(D.Read(role, "stamina"), CultureInfo.InvariantCulture) < 1)
                { Unknown("battle_launch_stamina_identity_changed"); return false; }
                staminaPending = false;
                bool result = staminaForward();
                if (!ObserveRequest(Opcode())) Unknown("battle_launch_stamina_request_unobserved");
                RestoreCompleted(); return result;
            }
            catch (Exception) { Unknown("battle_launch_stamina_invocation_unknown"); throw; }
            finally { Exit(); }
        }
        // Divinity's existing observer calls this after its exact stock 298.
        internal void FormationReturned()
        { formationReturned = true; RestoreCompleted(); }
        private void RestoreCompleted()
        {
            if (!formationReturned || scenarioPending || staminaPending || Error != "" ||
                !BattleRequestObserved && !cancelledBeforeBattle) return;
            // Compare-and-restore only our exact fields. Saved wrapper references
            // remain one-shot; stop/timeout never restores a live continuation.
            if (SameFormation())
            { attackField.SetValue(formation, attackOriginal); ((IDictionary)parameters)["OnAttack"] = attackOriginal; }
            if (replyField != null && ReferenceEquals(replyField.GetValue(responseHandler), replyWrapper) && handlers[(ushort)298] == null)
                replyField.SetValue(responseHandler, replyOriginal);
            if (scenarioField != null && ReferenceEquals(scenarioField.GetValue(scenario), scenarioWrapper))
                scenarioField.SetValue(scenario, null); // Consumed one-shot continuation must never be reopened.
            if (staminaField != null && ReferenceEquals(staminaField.GetValue(staminaMain), staminaWrapper))
                staminaField.SetValue(staminaMain, null);
        }
        internal void StartDirect(BattleLaunchAction invoke, ushort opcode)
        {
            if (!Enter()) throw new ProbeFailure(Error == "" ? "battle_launch_cancelled_before_request" : Error);
            try
            {
                if (handlers[opcode] != null) throw new ProbeFailure("battle_launch_battle_handler_already_registered");
                BattleLaunchInvoked = true; invoke();
                if (!ObserveRequest(opcode)) throw new ProbeFailure("battle_launch_direct_request_unobserved");
            }
            catch (Exception) { Unknown("battle_launch_direct_invocation_unknown"); throw; }
            finally { Exit(); }
        }
        internal void StartNonReport(BattleLaunchAction invoke, ushort opcode)
        { isNonReport = true; StartDirect(invoke, opcode); }
        internal void Validate()
        {
            if (BattleRequestObserved && initialReport == null && !NonReportReturned && Error == "" &&
                !ReferenceEquals(handlers[requestOpcode], requestHandler))
                Unknown("battle_launch_initial_handler_lost_unknown_no_retry");
            if (Unresolved && Error == "" && unchecked((uint)(Environment.TickCount - tick)) >= 180000)
                Unknown("battle_launch_pending_timeout_unknown_no_retry");
        }
        internal void CancelLocally() { locallyCancelled = true; }
        internal string Json()
        {
            string state = Error != "" ? "UNKNOWN" : cancelledBeforeBattle ? "CANCELLED_BEFORE_BATTLE" :
                nonReportOwned ? "NATIVE_NONREPORT_CONFIRMED" : reportTransferred ? "REPORT_TRANSFERRED" : BattleRequestObserved ? "REQUEST_OBSERVED" : locallyCancelled || Cancelled() ? "CANCEL_PENDING" : "PENDING";
            return "{\"interface\":" + D.Q(BattleLaunch.Interface) + ",\"epoch\":" + D.Q(epoch) +
                ",\"command_seq\":" + sequence + ",\"operation_id\":" + D.Q(operationId) + ",\"mode\":" + D.Q(mode.ToString()) + ",\"state\":" + D.Q(state) +
                ",\"reason\":" + D.Q(Error) + ",\"formation_update_attempted\":" + D.B(FormationUpdateAttempted) +
                ",\"battle_launch_invoked\":" + D.B(BattleLaunchInvoked) + ",\"battle_request_observed\":" + D.B(BattleRequestObserved) +
                ",\"initial_response_applied\":" + D.B(initialReport != null) + ",\"report_transferred\":" + D.B(reportTransferred) +
                ",\"nonreport_callback_returned\":" + D.B(NonReportReturned) +
                ",\"nonreport_result\":" + (NonReportReturned ? nonReportResult.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"unresolved\":" + D.B(Unresolved) +
                ",\"formation_callback_returned\":" + D.B(formationReturned) + "}";
        }
        private static FieldInfo Field(object receiver, string name)
        {
            for (Type type = receiver.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, D.Instance | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            throw new ProbeFailure("battle_launch_field_missing_" + name);
        }
    }
}
