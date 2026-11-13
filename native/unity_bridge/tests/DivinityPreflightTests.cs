using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using ProtoBuf;
using game.net.data.StoC;
using JiangShen.AssistantRuntime;
using M = JiangShen.AssistantRuntime.DailyDivinityBattleModel;
using F = JiangShen.AssistantRuntime.DailyDivinityBattleModel.Frame;
using W = BattleReplyPreflightWire;

internal static class DivinityPreflightTests
{
    private const string Operation = "00000000000000000000000000000008";
    private static int checks;
    private static void Check(bool condition, string why) { checks++; if (!condition) throw new Exception(why); }

    private sealed class Slot
    {
        internal DevelopmentNormalCallback callback, original;
        internal object arg;
        internal int Calls;
    }
    private sealed class Net
    {
        public object m_client=new DivinitySocket();
        public Stream m_stream=new MemoryStream(new byte[256]);
        public bool m_bConnectLost;
        internal readonly Dictionary<ushort, Slot> Slots = new Dictionary<ushort, Slot>();
        internal object Current(ushort opcode) { Slot slot; return Slots.TryGetValue(opcode, out slot) ? slot : null; }
        internal Slot Register(ushort opcode, Action<MemoryStream> stock)
        {
            Slot slot = new Slot { arg = opcode == M.FormationOpcode ? (object)true : null };
            slot.original = slot.callback = delegate(MemoryStream stream, object arg) {
                slot.Calls++; Check(ReferenceEquals(arg, slot.arg), "stock original arg"); if (stock != null) stock(stream);
            };
            Slots.Add(opcode, slot); return slot;
        }
        internal DailyDivinityReplyTap Attach(ushort opcode, DailyDivinityAdmissionCheck before, Action<byte[]> after)
        { return Attach(opcode,before,after,null); }
        internal DailyDivinityReplyTap Attach(ushort opcode, DailyDivinityAdmissionCheck before, Action<byte[]> after, Action<byte[]> preflight)
        {
            Slot slot = (Slot)Current(opcode);
            return slot == null ? null : DailyDivinityReplyTap.Attach(this, opcode == M.ActivityOpcode, slot,
                typeof(Slot).GetField("callback", BindingFlags.NonPublic | BindingFlags.Instance), slot.arg,
#if DIVINITY_PREFLIGHT_BASELINE
                delegate { return Current(opcode); }, before, after);
#else
                delegate { return Current(opcode); }, before, after, preflight);
#endif
        }
        internal void Dispatch(ushort opcode, MemoryStream body)
        {
            lock (this) {
                Slot slot = (Slot)Current(opcode); if (slot == null) return;
                if (opcode != M.ActivityOpcode) Slots.Remove(opcode);
                slot.callback(body, slot.arg);
            }
        }
    }
    private sealed class Fixture
    {
        internal readonly M Model = new M();
        internal readonly Net Client = new Net();
        internal readonly Database.Character Character=new Database.Character();
        internal readonly DivinityLoader Loader=new DivinityLoader();
        internal readonly DivinityPanel Panel=new DivinityPanel();
        internal readonly DivinityTroop Target=new DivinityTroop();
        internal readonly object GlobalRoot = new object(), HomeRoot = new object(),
            Home = new object(), Scene = new object(), Formation = new object(), Form = new object(), Attack = new object(),
            Rows = new object(), Listed = new object(), Reward = new object(), Template = new object(), RewardTemplate = new object(),
            Row = new object(), Battle = new object(), BattleRoot = new object();
        internal object Socket = new object(), Report;
        internal ulong Role = 78;
        internal uint Cycle = 19000, Now = 19500, Next = 104400, Cooldown;
        internal int Page, Tick = 10, Seq, FailCount, StockCalls, StartCalls;
        internal bool Battling, StopInStock, StopInFormation, SubstituteReport, ChangeOwnerInStock;
        internal MemoryStream LastStream;
        internal long LastPosition;
        internal readonly int Id;
        internal Fixture(int target = 4)
        {
            Id = target; Client.Register(M.ActivityOpcode, null);
            DivinityFixtureTypes.ClientType=typeof(Net);
            Global.character_=Character; Global.m_client=RootUI.m_client=Client; BaseAsset.lds=Loader;
            Loader.currmg=Scene; Panel.rmg=Scene; Panel.m_troop=Target; Target.idtroop=(byte)Id;
            Character.god_troops[Id]=Target; TimeUtil._now=Now; TimeUtil._thisDay=1000;
            Model.TestAttach = Client.Attach; Model.TestHandler = Client.Current;
            Model.TestBattle = delegate { return Battling ? new DailyDivinityBattleAnchor(Battle, BattleRoot, Report) : null; };
        }
        internal F Read()
        {
            F f = new F { Character=Character,Client=Client,Transport=DailyTransport.Read(Client),Loader=Loader,GlobalRoot=GlobalRoot,SocketWitness=Socket,Role=Role,
                Tick=Tick,Now=Now,Next=Next,Cycle=Cycle,Root=Page==0?HomeRoot:Scene,Home=Page==0,PanelReady=Page==1,FormReady=Page==2,
                HomeMain=Page==0?Home:null,Scene=Scene,Panel=Panel,Formation=Formation,OnAttack=Page==2?Attack:null,Form=Page==2?Form:null,FormId=3,
                Task=M.TaskId,TaskCategory=M.Category,Required=1,Count=0,Refresh=3,ListedCount=0,ListedRefresh=3,TaskTemplate=Template,
                RewardTemplate=RewardTemplate,Rows=Rows,Listed=Listed,Reward=Reward,Row=Row,ListedRow=Row,Selected=Target,SelectedData=Target.data_,
                SelectedIdentity=true,SelectedOwned=Page>=2,Troops=Character.god_troops,Target=Id,KilledMask=(1<<Id)-1,Cooldown=Cooldown,SelectedCooldown=Cooldown,
                Battle=Battle,BattleRoot=BattleRoot,BattleOwned=Battling,Battling=Battling,BattleVisible=Battling,Fighting=Battling,
                WaitEnd=Battling,UseEnd=Battling,ReportToken=Report };
            foreach (KeyValuePair<ushort,Slot> pair in Client.Slots) f.Handlers.Add(pair.Key,pair.Value);
            f.NetworkIdle=Client.Slots.Count==1; return f;
        }
        internal void Capture() { Tick++; Model.Observe(Read()); }
        internal bool Queue(string action) { string why; return Model.Queue(M.TaskId,Role,Operation,++Seq,Model.Revision,action,Id,out why); }
        internal Slot Start()
        {
            Capture(); Check(Queue("open")&&Model.BeginQueuedForTest(Read(),null),"open"); Page=1; Capture();
            Check(Queue("select")&&Model.BeginQueuedForTest(Read(),null),"select"); Page=2; Capture();
            Check(Queue("start")&&Model.BeginQueuedForTest(Read(),delegate {Client.Register(M.FormationOpcode,delegate {
                StartCalls++; Client.Register(M.BattleOpcode,StockBattle); Page=3; if(StopInFormation)Model.Stop();
            });}),"start formation");
            Client.Dispatch(M.FormationOpcode,new MemoryStream(new byte[]{8,1},false)); Capture();
            Check((Model.Status=="PENDING"||StopInFormation&&Model.OutcomeUnknown)&&StartCalls==1,"formation registered one battle");
            return (Slot)Client.Current(M.BattleOpcode);
        }
        private void StockBattle(MemoryStream stream)
        {
            StockCalls++; LastStream=stream; LastPosition=stream==null?-1:stream.Position;
            if (stream==null) return;
            SCGodBattle_0x4B1 packet=Serializer.Deserialize<SCGodBattle_0x4B1>(stream);
            // Pinned DivinityUI.sc_god_battle does not inspect result/target:
            // it loads report and then increments fail_count and writes cooldown.
            using(MemoryStream input=new MemoryStream(packet.report,false))
            using(Ionic.Zlib.DeflateStream zip=new Ionic.Zlib.DeflateStream(input,Ionic.Zlib.CompressionMode.Decompress))
                Report=Serializer.Deserialize<BattleReport>(zip);
            FailCount=Math.Min(35,FailCount+1); Cooldown=Target.data_.cd_time=packet.cooldown; Battling=true;
            if(SubstituteReport)Report=new object();
            if(ChangeOwnerInStock)Global.character_=new Database.Character();
            if(StopInStock) Model.Stop();
        }
    }
    private static byte[] Packet(uint target=4,uint status=1,bool targetPresent=true,bool winPresent=true,uint result=0,byte[] report=null)
    {
        return W.Join(W.N(1,result),targetPresent?W.N(2,target):new byte[0],winPresent?W.N(3,status==1?1u:0u):new byte[0],
            W.M(4,report??W.Report(status)),W.N(7,90));
    }
    private static void Negative()
    {
        foreach(string kind in new string[]{"wrong-target","bad-report"}) {
            Fixture f=new Fixture(); f.Start();
            byte[] packet=kind=="wrong-target"?Packet(3):Packet(4,1,true,true,0,new byte[]{8,1});
            try {f.Client.Dispatch(M.BattleOpcode,new MemoryStream(packet,false));} catch(Exception) { }
            f.Capture();
            Console.WriteLine("{\"scenario\":\""+kind+"\",\"stock\":"+f.StockCalls+",\"fail_count\":"+f.FailCount+",\"cooldown\":"+f.Cooldown+",\"unknown\":"+(f.Model.OutcomeUnknown?"true":"false")+"}");
            Check(f.StockCalls==1,"old source forwards untrusted reply before checking");
            if(kind=="wrong-target") Check(f.FailCount==1&&f.Cooldown==90,"old wrong target mutates business state");
        }
    }
#if !DIVINITY_PREFLIGHT_BASELINE
    private static void Valid(uint status, bool targetPresent, bool winPresent, int target)
    {
        Fixture f=new Fixture(target); Slot slot=f.Start(); DevelopmentNormalCallback saved=slot.callback;
        MemoryStream stream=new MemoryStream(Packet((uint)target,status,targetPresent,winPresent),false);
        f.Client.Dispatch(M.BattleOpcode,stream); f.Capture();
        Check(f.StockCalls==1&&f.FailCount==1&&f.Cooldown==90&&f.LastPosition==0&&ReferenceEquals(stream,f.LastStream),"valid stock once exact stream");
        Check(!f.Model.OutcomeUnknown&&f.Model.BattleWon==null&&f.StartCalls==1,"initial reply is not settlement or another launch");
        Check((uint)((BattleReport)f.Report).result_status==status,"real native decompressed result retained");
        Check(f.Model.Json().Contains("\"initial_response_binding\":\""+(targetPresent?"target-echo":"opcode-only")+"\"")&&
            f.Model.Json().Contains("\"initial_outcome_binding\":\""+(winPresent?"outcome-echo":"report-only")+"\""),"missing optional echoes are labelled honestly");
        saved(new MemoryStream(Packet(),false),null); f.Capture();
        Check(f.StockCalls==1&&f.FailCount==1&&f.StartCalls==1,"duplicate cannot apply twice or launch");
    }
    private static void Blocked(byte[] body, Action<Fixture,Slot> change=null, bool stop=false, bool retained=false)
    {
        Fixture f=new Fixture(); Slot slot=f.Start(); DevelopmentNormalCallback saved=slot.callback;
        if(stop)f.Model.Stop(); if(change!=null)change(f,slot);
        MemoryStream stream=body==null?null:new MemoryStream(body,false);
        if(retained)saved(stream,null);else f.Client.Dispatch(M.BattleOpcode,stream);
        f.Capture();
        Check(f.StockCalls==0&&f.FailCount==0&&f.Cooldown==0&&f.StartCalls==1,"untrusted reply has no stock/target effects or new sends");
        Check(f.Model.OutcomeUnknown&&f.Model.BattleWon==null&&!f.Model.TaskConfirmed,"untrusted reply remains UNKNOWN without win/loss counts");
        if(stream!=null)Check(stream.Position==0,"blocked input cursor untouched");
        saved(new MemoryStream(Packet(),false),null);f.Capture();
        Check(f.StockCalls==0&&f.FailCount==0&&f.StartCalls==1,"failed reply cannot be repaired by saved callback or retry");
    }
    private static void Stopped(bool duringStock,bool duringFormation=false)
    {
        Fixture f=new Fixture();f.StopInStock=duringStock;f.StopInFormation=duringFormation;
        Slot slot=f.Start(); DevelopmentNormalCallback saved=slot.callback;
        if(!duringStock&&!duringFormation)f.Model.Stop();
        Check(!ReferenceEquals(slot.callback,slot.original),"stopped pending reply retains validating wrapper");
        MemoryStream stream=new MemoryStream(Packet(),false);f.Client.Dispatch(M.BattleOpcode,stream);f.Capture();
        Check(f.StockCalls==1&&f.FailCount==1&&f.Cooldown==90&&ReferenceEquals(stream,f.LastStream)&&f.LastPosition==0,"known sent request may finish stock once after stop");
        Check(f.Model.OutcomeUnknown&&f.Model.BattleWon==null&&!f.Model.TaskConfirmed&&f.StartCalls==1&&!f.Queue("start"),"stop does not promote result or admit another battle");
        saved(new MemoryStream(Packet(),false),null);f.Capture();
        Check(f.StockCalls==1&&f.FailCount==1&&f.StartCalls==1,"stopped duplicate remains one-shot");
    }
    private static void DirectRace(string kind)
    {
        Net net=new Net();Slot slot=net.Register(M.BattleOpcode,delegate{});bool owner=true;int stock=0;
        slot.original=slot.callback=delegate{stock++;};
        DailyDivinityReplyTap tap=null;MemoryStream stream=new MemoryStream(Packet(),false);
        FieldInfo field=typeof(Slot).GetField("callback",BindingFlags.NonPublic|BindingFlags.Instance);
        tap=DailyDivinityReplyTap.Attach(net,false,slot,field,null,delegate{return net.Current(M.BattleOpcode);},delegate{return owner;},delegate{},delegate(byte[] body){
            DailyDivinityInitialReply.Decode(body,4);
            if(kind=="owner")owner=false;
            else if(kind=="callback")slot.callback=slot.original;
            else if(kind=="arg")slot.arg=new object();
            else if(kind=="cursor")stream.Position=1;
            else if(kind=="handler"){net.Slots.Remove(M.BattleOpcode);net.Register(M.BattleOpcode,delegate{});}
            else if(kind=="stop")tap.StopObserving(true);
        });
        DevelopmentNormalCallback saved=slot.callback;
        net.Slots.Remove(M.BattleOpcode);saved(stream,null);
        Check(stock==(kind=="stop"?1:0),"post-decode boundary must still own input/handler/arg/role");
        Check(kind=="stop"?tap.Returned:tap.Error!=null,"race result is proven callback or unknown");
        saved(new MemoryStream(Packet(),false),null);Check(stock==(kind=="stop"?1:0),"race duplicates never forward");
    }
    private static int scenarios,failures;
    private static void Run(string name,Action test)
    {
        scenarios++;int before=checks;
        try{test();Console.WriteLine("{\"scenario\":\""+name+"\",\"status\":\"PASS\",\"checks\":"+(checks-before)+"}");}
        catch(Exception e){failures++;Console.WriteLine("{\"scenario\":\""+name+"\",\"status\":\"FAIL\",\"error\":\""+e.ToString().Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r"," ").Replace("\n"," ")+"\"}");}
    }
    private static void Suite()
    {
        foreach(uint s in new uint[]{1,2,3})foreach(bool echo in new bool[]{false,true}){
            uint status=s;bool present=echo;Run("valid-status-"+s+"-outcome-echo-"+echo,delegate{Valid(status,true,present,4);});
        }
        Run("valid-native-default-target-zero",delegate{Valid(1,false,false,0);});
        Run("absent-target-echo-local-owner-only",delegate{Valid(1,false,false,4);});
        Run("wrong-target",delegate{Blocked(Packet(3));});
        Run("missing-result",delegate{Blocked(W.Join(W.N(2,4),W.M(4,W.Report(1))));});
        Run("unknown-result",delegate{Blocked(Packet(4,1,true,true,9));});
        Run("missing-report",delegate{Blocked(W.Join(W.N(1,0),W.N(2,4)));});
        Run("empty-report",delegate{Blocked(Packet(4,1,true,true,0,new byte[0]));});
        Run("bad-compression",delegate{Blocked(Packet(4,1,true,true,0,new byte[]{8,1}));});
        byte[] good=W.Report(1),cut=new byte[good.Length-1];Array.Copy(good,cut,cut.Length);
        Run("truncated-compression",delegate{Blocked(Packet(4,1,true,true,0,cut));});
        Run("trailing-compression",delegate{Blocked(Packet(4,1,true,true,0,W.Join(good,new byte[]{0})));});
        Run("missing-report-required",delegate{Blocked(Packet(4,1,true,true,0,W.Compress(W.N(3,1))));});
        Run("report-unknown-result",delegate{Blocked(Packet(4,99));});
        Run("outer-report-conflict",delegate{Blocked(Packet(4,1,true,true,0,W.Report(2)));});
        Run("inflated-report-bound",delegate{Blocked(Packet(4,1,true,true,0,W.Compress(new byte[BattleReplyPreflight.MaxInflatedBytes+1])));});
        Run("packet-bound-before-copy",delegate{Blocked(new byte[BattleReplyPreflight.MaxPacketBytes+1]);});
        Run("null-timeout",delegate{Blocked(null);});
        foreach(int t in new int[]{1,2,3,4,7}){int tag=t;Run("duplicate-tag-"+tag,delegate{Blocked(W.Join(Packet(),tag==4?W.M(4,good):W.N(tag,0)));});}
        Run("unknown-field",delegate{Blocked(W.Join(Packet(),W.N(99,0)));});
        Run("wrong-scalar-wire",delegate{Blocked(W.Join(W.N(1,0),W.M(2,new byte[]{4}),W.M(4,good)));});
        Run("invalid-bool",delegate{Blocked(W.Join(W.N(1,0),W.N(2,4),W.N(3,2),W.M(4,good)));});
        Run("scalar-overflow",delegate{Blocked(W.Join(W.N(1,0),W.N(2,0x100000000UL),W.M(4,good)));});
        Run("stop-before-reply",delegate{Stopped(false);});
        Run("repeated-stop-before-reply",delegate{
            Fixture f=new Fixture();f.Start();f.Model.Stop();f.Model.Stop();f.Client.Dispatch(M.BattleOpcode,new MemoryStream(Packet(),false));f.Capture();
            Check(f.StockCalls==1&&f.FailCount==1&&f.Model.OutcomeUnknown&&f.StartCalls==1,"repeated Stop cannot revoke the owned reply or start another battle");
        });
        Run("stop-in-original-callback",delegate{Stopped(true);});
        Run("stop-in-formation-handoff",delegate{Stopped(false,true);});
        Run("stopped-wrong-target",delegate{Blocked(Packet(3),null,true);});
        Run("stopped-bad-report",delegate{Blocked(Packet(4,1,true,true,0,new byte[]{8,1}),null,true);});
        Run("stopped-timeout",delegate{Blocked(null,null,true);});
        Dictionary<string,Action<Fixture,Slot>> changes=new Dictionary<string,Action<Fixture,Slot>>{
            {"role-object",delegate(Fixture f,Slot s){Global.character_=new Database.Character();}},
            {"role-id",delegate(Fixture f,Slot s){f.Character.id++;}},
            {"target-table",delegate(Fixture f,Slot s){f.Character.god_troops=new DivinityTroop[5];}},
            {"target-id",delegate(Fixture f,Slot s){f.Target.idtroop=3;}},
            {"target-data",delegate(Fixture f,Slot s){f.Target.data_=new DivinityTroopData();}},
            {"target-killed",delegate(Fixture f,Slot s){f.Target.data_.killed=true;}},
            {"target-cooldown",delegate(Fixture f,Slot s){f.Target.data_.cd_time=1;}},
            {"panel-target",delegate(Fixture f,Slot s){f.Panel.m_troop=new object();}},
            {"panel-root",delegate(Fixture f,Slot s){f.Panel.rmg=new object();}},
            {"loader-root",delegate(Fixture f,Slot s){f.Loader.currmg=new object();}},
            {"loader-object",delegate(Fixture f,Slot s){BaseAsset.lds=new object();}},
            {"current-client",delegate(Fixture f,Slot s){RootUI.m_client=new object();}},
            {"socket",delegate(Fixture f,Slot s){f.Client.m_client=new DivinitySocket();}},
            {"transport-stream",delegate(Fixture f,Slot s){f.Client.m_stream=new MemoryStream(new byte[256]);}},
            {"disconnected",delegate(Fixture f,Slot s){((DivinitySocket)f.Client.m_client).Connected=false;}},
            {"connect-lost",delegate(Fixture f,Slot s){f.Client.m_bConnectLost=true;}},
            {"clock-cycle",delegate(Fixture f,Slot s){TimeUtil._now=TimeUtil._thisDay+18000-1;}},
            {"refresh-next",delegate(Fixture f,Slot s){f.Character.next_resource_time++;}},
            {"handler-argument",delegate(Fixture f,Slot s){s.arg=new object();}},
            {"handler-callback",delegate(Fixture f,Slot s){s.callback=s.original;}},
            {"handler-replaced",delegate(Fixture f,Slot s){f.Client.Slots.Remove(M.BattleOpcode);f.Client.Register(M.BattleOpcode,delegate{});}}
        };
        foreach(KeyValuePair<string,Action<Fixture,Slot>> pair in changes){Action<Fixture,Slot> change=pair.Value;string name=pair.Key;
            Run("owner-"+name,delegate{Blocked(Packet(),change,false,true);});
            Run("stopped-owner-"+name,delegate{Blocked(Packet(),change,true,true);});
        }
        foreach(string r in new string[]{"owner","callback","arg","cursor","handler","stop"}){string kind=r;Run("decode-race-"+kind,delegate{DirectRace(kind);});}
        Run("callback-wrong-thread",delegate{
            Fixture f=new Fixture();Slot slot=f.Start();DevelopmentNormalCallback saved=slot.callback;
            f.Client.Slots.Remove(M.BattleOpcode);Thread t=new Thread(delegate(){saved(new MemoryStream(Packet(),false),null);});t.Start();Check(t.Join(2000),"worker joined");f.Capture();
            Check(f.StockCalls==0&&f.Model.OutcomeUnknown,"wrong thread cannot reach stock");
        });
        Run("callback-lock-contention",delegate{
            Fixture f=new Fixture();Slot slot=f.Start();DevelopmentNormalCallback saved=slot.callback;
            using(ManualResetEvent held=new ManualResetEvent(false))using(ManualResetEvent release=new ManualResetEvent(false)){
                Thread t=new Thread(delegate(){lock(f.Client){held.Set();release.WaitOne(2000);}});t.Start();Check(held.WaitOne(2000),"client held");
                saved(new MemoryStream(Packet(),false),null);release.Set();Check(t.Join(2000),"lock worker joined");
            }f.Capture();Check(f.StockCalls==0&&f.Model.OutcomeUnknown,"contended callback fails closed without blocking");
        });
        Run("post-report-substitution",delegate{Fixture f=new Fixture();f.SubstituteReport=true;f.Start();f.Client.Dispatch(M.BattleOpcode,new MemoryStream(Packet(),false));f.Capture();Check(f.StockCalls==1&&f.Model.OutcomeUnknown&&f.Model.BattleWon==null,"native changed report stays unknown");});
        Run("post-owner-change",delegate{Fixture f=new Fixture();f.ChangeOwnerInStock=true;f.Start();f.Client.Dispatch(M.BattleOpcode,new MemoryStream(Packet(),false));f.Capture();Check(f.StockCalls==1&&f.Model.OutcomeUnknown&&f.Model.BattleWon==null,"native changed owner stays unknown");});
    }
#else
    private static int scenarios,failures;
#endif
    private static int Main(string[] args)
    {
        try {
#if DIVINITY_PREFLIGHT_BASELINE
            Negative();
#else
            if(args.Length>0&&args[0]=="negative")Negative();else Suite();
#endif
            Console.WriteLine("{\"status\":\""+(failures==0?"PASS":"FAIL")+"\",\"scenarios\":"+scenarios+",\"checks\":"+checks+",\"failed\":"+failures+",\"game_access\":false}");return failures==0?0:1;}
        catch(Exception e){Console.WriteLine("{\"status\":\"FAIL\",\"checks\":"+checks+",\"error\":\""+e.ToString().Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r"," ").Replace("\n"," ")+"\"}");return 1;}
    }
}
