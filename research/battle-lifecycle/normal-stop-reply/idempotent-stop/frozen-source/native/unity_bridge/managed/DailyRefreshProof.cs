using System;
using System.Collections;
using System.IO;
using System.Threading;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal sealed class DailyTransport
    {
        private readonly object client, socket;
        private readonly Stream stream;
        private DailyTransport(object client, object socket, Stream stream)
        { this.client = client; this.socket = socket; this.stream = stream; }

        internal bool Same(DailyTransport other)
        {
            return other != null && object.ReferenceEquals(client, other.client) &&
                object.ReferenceEquals(socket, other.socket) && object.ReferenceEquals(stream, other.stream);
        }

        internal bool StillCurrent()
        {
            try { return Same(Read(client)); }
            catch (Exception) { return false; }
        }

        internal static DailyTransport Read(object client)
        {
            if (client == null || client.GetType() != Model.FirstType("game.net.tcp_client") || !Healthy(client)) return null;
            object socket = D.Read(client, "m_client");
            Stream stream = D.Read(client, "m_stream") as Stream;
            if (socket == null || stream == null || !stream.CanRead || !stream.CanWrite ||
                !(bool)D.Method(socket.GetType(), "get_Connected", Type.EmptyTypes, false, typeof(bool)).Invoke(socket, null) ||
                !object.ReferenceEquals(socket, D.Read(client, "m_client")) || !object.ReferenceEquals(stream, D.Read(client, "m_stream")) ||
                !Healthy(client)) return null;
            return new DailyTransport(client, socket, stream);
        }
        private static bool Healthy(object client)
        {
            return D.Int(D.Static(Model.FirstType("game.net.tcp_base"), "connection_state")) == 3 &&
                !D.Bool(D.Read(client, "m_bConnectLost")) && !D.Bool(D.Static(Model.FirstType("Global"), "relogin"));
        }

        internal static object CurrentClient(object character)
        {
            Type global = Model.FirstType("Global");
            if (character == null || character.GetType() != Model.FirstType("Database.Character") ||
                !object.ReferenceEquals(D.Static(global, "character_"), character)) return null;
            object client = D.Static(Model.GameType("RootUI"), "m_client");
            return object.ReferenceEquals(client, D.Static(global, "m_client")) ? client : null;
        }
    }

    // One bridge session owns the proof and one passive observer for the game's
    // permanent 0x24 callback. This observer never sends a refresh.
    internal static class DailyRefreshProof
    {
        private sealed class Proof
        {
            internal object Role;
            internal ulong RoleId;
            internal DailyTransport Transport;
            internal uint Cycle, Next;
        }
        private static Proof proof, naturalCandidate;
        private static object watchedRole, watchedHandler;
        private static DailyTransport watchedTransport;
        private static DevelopmentCallbackCapture clockCapture;
        private static readonly Type[] ResponseArgs = new Type[] { typeof(MemoryStream), typeof(object) };

        internal static bool Verified(object role, uint cycle)
        {
            Proof p = proof;
            if (p == null || !object.ReferenceEquals(role, p.Role) || p.Cycle != cycle ||
                clockCapture != null && clockCapture.Read().Calls != 0 ||
                (ulong)D.Read(role, "id") != p.RoleId || (uint)D.Read(role, "next_resource_time") != p.Next) return false;
            return p.Transport.Same(DailyTransport.Read(DailyTransport.CurrentClient(role)));
        }

        // Invoked only after the owned 16 callback and the independent rebuilt
        // cache comparison have both completed.
        internal static bool RecordSnapshot(object role, DailyTransport transport, uint cycle, uint next)
        {
            uint now, day, current;
            if (transport == null || DailyRefreshGate.ReadClock(role, out now, out day, out current) != "" ||
                current != cycle || next != (uint)D.Read(role, "next_resource_time") ||
                !transport.Same(DailyTransport.Read(DailyTransport.CurrentClient(role)))) return false;
            proof = new Proof { Role = role, RoleId = (ulong)D.Read(role, "id"), Transport = transport, Cycle = cycle, Next = next };
            return true;
        }

        internal static void ObserveNatural()
        {
            try
            {
                object role = D.Static(Model.FirstType("Global"), "character_");
                object client = DailyTransport.CurrentClient(role);
                DailyTransport transport = DailyTransport.Read(client);
                if (transport == null)
                { proof = null; DetachClock(); return; }
                if (proof != null && (!object.ReferenceEquals(role, proof.Role) || !proof.Transport.Same(transport))) proof = null;
                if (clockCapture != null && (!object.ReferenceEquals(role, watchedRole) || !transport.Same(watchedTransport)))
                    if (!DetachClock()) return;
                if (!Monitor.TryEnter(client, 0)) return;
                try { ObserveLocked(role, client, transport); }
                finally { Monitor.Exit(client); }
            }
            catch (Exception) { proof = null; DetachClock(); }
        }

        private static void ObserveLocked(object role, object client, DailyTransport transport)
        {
            IDictionary handlers = D.Read(client, "m_handlers") as IDictionary;
            object handler = handlers == null ? null : handlers[(ushort)36];
            if (clockCapture != null)
            {
                if (!object.ReferenceEquals(handler, watchedHandler) || !clockCapture.StillInstalled())
                { proof = null; if (!DetachClock()) return; }
                else
                {
                    DevelopmentCallbackWitness witness = clockCapture.Read();
                    if (witness.Calls == 0 && witness.Armed) return;
                    proof = null;
                    Proof p = naturalCandidate;
                    if (witness.Calls == 1 && witness.Returned && !witness.NullResponse && !witness.OriginalThrew &&
                        witness.Error == null && p != null && object.ReferenceEquals(role, p.Role) && (ulong)D.Read(role, "id") == p.RoleId)
                        RecordSnapshot(role, p.Transport, p.Cycle, p.Next);
                    if (!DetachClock()) return;
                }
            }
            if (handler == null) return;
            Delegate original = D.Read(handler, "handler") as Delegate;
            Type root = Model.GameType("RootUI");
            if (original == null || original.Target != null || original.GetInvocationList().Length != 1 ||
                original.Method.DeclaringType != root ||
                original.Method != D.Method(root, "sc_player_refresh_fiveclock_0x24", ResponseArgs, true, typeof(void)) ||
                D.Read(handler, "arg") != null || (float)D.Read(handler, "life") != -1f) return;
            watchedHandler = handler; watchedRole = role; watchedTransport = transport;
            string why;
            clockCapture = DevelopmentCallbackCapture.InstallDailyClock36(client, handler, original,
                delegate(byte[] body) { CaptureClockBody(role, transport, body); }, out why);
        }

        private static void CaptureClockBody(object role, DailyTransport transport, byte[] body)
        {
            proof = null;
            naturalCandidate = null;
            uint now, day, cycle;
            if (!transport.Same(DailyTransport.Read(DailyTransport.CurrentClient(role))) ||
                DailyRefreshGate.ReadClock(role, out now, out day, out cycle) != "daily_refresh_pending" ||
                !D.Bool(D.Static(Model.GameType("RootUI"), "m_bRefreshFive")) ||
                (uint)D.Read(role, "next_resource_time") > now || cycle == 0) return;
            uint result, next;
            DailyPlayerSnapshot.ReadClock36(body, out result, out next);
            if (result != 0 || next <= now || next != (long)cycle + 86400L) return;
            naturalCandidate = new Proof { Role = role, RoleId = (ulong)D.Read(role, "id"), Transport = transport, Cycle = cycle, Next = next };
        }

        private static bool DetachClock()
        {
            naturalCandidate = null;
            if (clockCapture != null && clockCapture.DisarmAndRestore().EndsWith("forward_only", StringComparison.Ordinal)) return false;
            clockCapture = null; watchedRole = watchedHandler = null; watchedTransport = null;
            return true;
        }

        internal static bool Stop() { proof = null; return DetachClock(); }
    }
}
