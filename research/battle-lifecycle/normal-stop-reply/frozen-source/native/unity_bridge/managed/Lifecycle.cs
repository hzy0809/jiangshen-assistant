using System;

namespace JiangShen.AssistantRuntime
{
    internal sealed class SessionLifecycle
    {
        internal const int LeaseMilliseconds = 20000;
        internal const int RecoveryMilliseconds = 10000;
        private uint touched;
        internal int LastSequence;
        internal bool StopRequested, Destroyed, ActionPending, RecoveryUnknown;
        internal string StopReason = "";

        internal SessionLifecycle(int now) { touched = unchecked((uint)now); }
        internal bool LeaseExpired(int now) { return unchecked((uint)now - touched) >= LeaseMilliseconds; }
        internal bool Accept(Command command, int now, out string reason)
        {
            reason = "";
            if (Destroyed || StopRequested) { reason = "stopping"; return false; }
            if (LeaseExpired(now)) { RequestStop("heartbeat_timeout"); reason = "heartbeat_timeout"; return false; }
            if (command == null || command.Sequence <= LastSequence) { reason = "sequence"; return false; }
            LastSequence = command.Sequence;
            if (ActionPending && command.Name != "stop") { reason = "busy"; return false; }
            touched = unchecked((uint)now);
            if (command.Name == "stop") RequestStop("stop");
            return true;
        }
        internal bool BeginAction()
        {
            if (Destroyed || StopRequested || ActionPending || RecoveryUnknown) return false;
            ActionPending = true; return true;
        }
        internal void CompleteAction(bool restorationVerified)
        {
            ActionPending = false;
            if (!restorationVerified) { RecoveryUnknown = true; RequestStop("recovery_unknown"); }
        }
        internal void RequestStop(string reason)
        {
            if (!StopRequested) { StopRequested = true; StopReason = reason; }
        }
        internal void MarkDestroyed() { Destroyed = true; StopRequested = true; }
    }

    // This per-assembly guard is supplemented by an OS existence guard shared by
    // every app-specific assembly version in this target process.
    internal static class SessionRegistry
    {
        private static string owner;
        private static bool unknown;
        internal static void ResetForRetry() { owner = null; unknown = false; }
        internal static bool Acquire(string nonce)
        {
            if (!Command.ValidNonce(nonce) || owner != null || unknown) return false;
            owner = nonce; return true;
        }
        internal static void ReleaseFromOnDestroy(string nonce, bool unresolved)
        {
            if (owner != nonce) return;
            unknown = unknown || unresolved; owner = null;
        }
        internal static bool RecoveryUnknown { get { return unknown; } }
    }
}
