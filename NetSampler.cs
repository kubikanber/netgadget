// Ağ Etkinliği gadget'ı için örnekleyici.
// Yönetici modunda Microsoft-Windows-Kernel-Network ETW sağlayıcısından her TCP/UDP
// paketini (IPv4 + IPv6) sahip süreciyle alır. ETW açılamazsa TCP EStats sayaçlarına,
// yönetici değilse yalnızca TCP bağlantı tablosuna düşer.
// PowerShell 5.1 Add-Type (C# 5) ile derlenir: C# 6+ sözdizimi kullanmayın.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NetGadget
{
    public class EndpointStat
    {
        public string Protocol;   // "TCP" / "UDP"
        public string Address;
        public int Port;
        public string Host;       // DNS adı (biliniyorsa)
        public string Service;    // port adı (HTTPS, QUIC, DNS...)
        public bool Discovery;    // yerel ağ keşfi/duyurusu (mDNS, SSDP, LLMNR, çok noktaya yayın...)
        public bool Multicast;
        public bool Open;         // şu an açık TCP bağlantısı var mı
        public int Count;         // bu uca açık TCP bağlantı sayısı
        public double InRate;
        public double OutRate;
        public long TotalIn;
        public long TotalOut;
        public DateTime LastSeen; // UTC
        public DateTime FirstSeen; // UTC

        // İnceleme (Inspector) alanları
        public bool Public;       // internet adresi (özel/yerel ağ değil)
        public bool Inbound;      // karşı taraf bağlandı (bizim dinlediğimiz porta)
        public bool NoDns;        // bu IP için DNS yanıtı görülmedi
        public bool IsNew;
        public bool Trusted;
        public string DomainKey;
        public List<string> Flags = new List<string>();

        public double Total { get { return InRate + OutRate; } }
        public string Display { get { return string.IsNullOrEmpty(Host) ? Address : Host; } }
    }

    public class AppStat
    {
        public string Key;
        public string Name;
        public string Path;
        public string Services;
        public List<int> Pids = new List<int>();
        public int TcpCount;
        public int UdpCount;
        public int LocalDevices;  // keşif trafiğindeki yerel ağ cihazı sayısı
        public double InRate;
        public double OutRate;
        public long TotalIn;
        public long TotalOut;
        public List<EndpointStat> Endpoints = new List<EndpointStat>();
        public List<EndpointStat> Discovery = new List<EndpointStat>();
        public List<string> Listening = new List<string>();   // dinlenen TCP adresleri (loopback hariç)
        public bool ListensAll;                                // 0.0.0.0 / [::] üzerinde dinliyor

        // İnceleme (Inspector) alanları
        public string StoreKey;
        public int Score;
        public int Level;          // 0 yok, 1 not, 2 dikkat, 3 şüpheli
        public List<string> Reasons = new List<string>();
        public bool IsNew;
        public bool Trusted;
        public string SignStatus;  // Valid, NotSigned, Invalid, Unknown (null: henüz bakılmadı)
        public string Signer;
        public string CommandLine;
        public int ParentPid;
        public string ParentName;
        public string User;
        public DateTime StartTime;

        public double Total { get { return InRate + OutRate; } }
        public int Connections { get { return TcpCount + UdpCount + Discovery.Count; } }
    }

    struct FlowKey : IEquatable<FlowKey>
    {
        public int Pid;
        public byte Proto;   // 6 = TCP, 17 = UDP
        public bool V6;
        public ulong Hi, Lo; // uzak adresin ham baytları (IPv4: Lo)
        public ushort Port;  // uzak port

        public bool Equals(FlowKey o)
        {
            return Pid == o.Pid && Proto == o.Proto && V6 == o.V6 && Hi == o.Hi && Lo == o.Lo && Port == o.Port;
        }

        public override bool Equals(object o) { return o is FlowKey && Equals((FlowKey)o); }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Pid * 31 + Proto;
                h = h * 31 + Port;
                h = h * 31 + Lo.GetHashCode();
                h = h * 31 + Hi.GetHashCode();
                return V6 ? ~h : h;
            }
        }

        public string AddressText()
        {
            if (!V6) return new IPAddress((long)(Lo & 0xFFFFFFFF)).ToString();
            var b = new byte[16];
            Array.Copy(BitConverter.GetBytes(Hi), 0, b, 0, 8);
            Array.Copy(BitConverter.GetBytes(Lo), 0, b, 8, 8);
            var ip = new IPAddress(b);
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            return ip.ToString();
        }
    }

    class Flow
    {
        public string Ip;
        public long TickIn, TickOut, TotalIn, TotalOut;
        public DateTime LastSeen, FirstSeen;
        public bool Open;
        public bool Public;
        public bool Inbound;
        public int Conns;
    }

    class ProcEntry
    {
        public int Pid;
        public string Key, Name, Path;
        public bool IsSvchost;
    }

    // Kernel-Network sağlayıcısını gerçek zamanlı bir ETW oturumunda dinler.
    class EtwNet : IDisposable
    {
        const string SessionName = "NetGadget-KernelNetwork";
        static readonly Guid KernelNetwork = new Guid("7DD42A49-5329-4832-8DFD-43D979153A88");
        static readonly Guid DnsClient = new Guid("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");
        static readonly long DnsGuidA = BitConverter.ToInt64(DnsClient.ToByteArray(), 0);
        static readonly long DnsGuidB = BitConverter.ToInt64(DnsClient.ToByteArray(), 8);
        const uint EVENT_TRACE_CONTROL_STOP = 1;
        const uint ERROR_ALREADY_EXISTS = 183;

        [StructLayout(LayoutKind.Sequential)]
        struct EVENT_TRACE_HEADER
        {
            public ushort Size, FieldTypeFlags;
            public uint Version, ThreadId, ProcessId;
            public long TimeStamp;
            public Guid Guid;
            public uint KernelTime, UserTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct EVENT_TRACE
        {
            public EVENT_TRACE_HEADER Header;
            public uint InstanceId, ParentInstanceId;
            public Guid ParentGuid;
            public IntPtr MofData;
            public uint MofLength, ClientContext;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TRACE_LOGFILE_HEADER
        {
            public uint BufferSize, Version, ProviderVersion, NumberOfProcessors;
            public long EndTime;
            public uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten;
            public Guid LogInstanceGuid;
            public IntPtr LoggerName, LogFileName;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 172)] public byte[] TimeZone;
            public long BootTime, PerfFreq, StartTime;
            public uint ReservedFlags, BuffersLost;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate void EventRecordCallback(IntPtr record);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct EVENT_TRACE_LOGFILE
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string LogFileName;
            [MarshalAs(UnmanagedType.LPWStr)] public string LoggerName;
            public long CurrentTime;
            public uint BuffersRead;
            public uint ProcessTraceMode;
            public EVENT_TRACE CurrentEvent;
            public TRACE_LOGFILE_HEADER LogfileHeader;
            public IntPtr BufferCallback;
            public uint BufferSize, Filled, EventsLost;
            public EventRecordCallback EventRecordCallback;
            public uint IsKernelTrace;
            public IntPtr Context;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern uint StartTrace(out ulong sessionHandle, string sessionName, IntPtr properties);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern uint ControlTrace(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);

        [DllImport("advapi32.dll")]
        static extern uint EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level,
            ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern ulong OpenTrace(ref EVENT_TRACE_LOGFILE logfile);

        [DllImport("advapi32.dll")]
        static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

        [DllImport("advapi32.dll")]
        static extern uint CloseTrace(ulong traceHandle);

        readonly object gate = new object();
        Dictionary<FlowKey, long[]> pending = new Dictionary<FlowKey, long[]>();
        EventRecordCallback callback;
        ulong session, trace;
        bool running;
        long eventCount;

        public Action<string, string> DnsAnswer;   // (ip, ad)
        public volatile HashSet<uint> Local4 = new HashSet<uint>();
        public volatile HashSet<ulong> Local6 = new HashSet<ulong>();
        public string Error;
        public long EventCount { get { return Interlocked.Read(ref eventCount); } }

        public static ulong Hash6(ulong hi, ulong lo) { return lo ^ (hi * 0x9E3779B97F4A7C15UL); }

        public bool Start()
        {
            StopByName();
            IntPtr props = BuildProps();
            try
            {
                uint r = StartTrace(out session, SessionName, props);
                if (r == ERROR_ALREADY_EXISTS)
                {
                    StopByName();
                    r = StartTrace(out session, SessionName, props);
                }
                if (r != 0) { Error = "StartTrace " + r; return false; }
            }
            finally { Marshal.FreeHGlobal(props); }

            Guid provider = KernelNetwork;
            uint e = EnableTraceEx2(session, ref provider, 1 /* ENABLE */, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);
            if (e != 0) { Error = "EnableTraceEx2 " + e; StopByName(); return false; }
            provider = DnsClient;
            EnableTraceEx2(session, ref provider, 1, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);   // alan adları için; başarısızlık önemsiz

            callback = OnEvent;
            var lf = new EVENT_TRACE_LOGFILE();
            lf.LoggerName = SessionName;
            lf.ProcessTraceMode = 0x00000100 /* REAL_TIME */ | 0x10000000 /* EVENT_RECORD */;
            lf.EventRecordCallback = callback;
            lf.LogfileHeader.TimeZone = new byte[172];
            trace = OpenTrace(ref lf);
            if (trace == ulong.MaxValue || trace == 0xFFFFFFFFUL)
            {
                Error = "OpenTrace " + Marshal.GetLastWin32Error();
                StopByName();
                return false;
            }

            running = true;
            var t = new Thread(delegate() { ProcessTrace(new ulong[] { trace }, 1, IntPtr.Zero, IntPtr.Zero); });
            t.IsBackground = true;
            t.Name = "NetGadget ETW";
            t.Start();
            return true;
        }

        static IntPtr BuildProps()
        {
            const int header = 120;
            int size = header + 2048 * 2;
            IntPtr p = Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size], 0, p, size);
            Marshal.WriteInt32(p, 0, size);                          // Wnode.BufferSize
            Marshal.Copy(Guid.NewGuid().ToByteArray(), 0, new IntPtr(p.ToInt64() + 24), 16);
            Marshal.WriteInt32(p, 40, 1);                            // Wnode.ClientContext = QPC
            Marshal.WriteInt32(p, 44, 0x00020000);                   // WNODE_FLAG_TRACED_GUID
            Marshal.WriteInt32(p, 48, 64);                           // BufferSize (KB)
            Marshal.WriteInt32(p, 52, 4);                            // MinimumBuffers
            Marshal.WriteInt32(p, 56, 64);                           // MaximumBuffers
            Marshal.WriteInt32(p, 64, 0x00000100);                   // EVENT_TRACE_REAL_TIME_MODE
            Marshal.WriteInt32(p, 68, 1);                            // FlushTimer (sn)
            Marshal.WriteInt32(p, IntPtr.Size == 8 ? 116 : 112, header); // LoggerNameOffset
            return p;
        }

        static void StopByName()
        {
            IntPtr props = BuildProps();
            try { ControlTrace(0, SessionName, props, EVENT_TRACE_CONTROL_STOP); }
            finally { Marshal.FreeHGlobal(props); }
        }

        static ushort Swap(short v)
        {
            return (ushort)(((v & 0xFF) << 8) | ((v >> 8) & 0xFF));
        }

        void OnEvent(IntPtr rec)
        {
            try
            {
                int id = (ushort)Marshal.ReadInt16(rec, 40);
                if (Marshal.ReadInt64(rec, 24) == DnsGuidA && Marshal.ReadInt64(rec, 32) == DnsGuidB)
                {
                    if (id == 3008) OnDnsEvent(rec);
                    return;
                }
                bool send, v6;
                byte proto;
                switch (id)
                {
                    case 10: send = true; v6 = false; proto = 6; break;   // TCPv4 gönderim
                    case 11: send = false; v6 = false; proto = 6; break;  // TCPv4 alım
                    case 26: send = true; v6 = true; proto = 6; break;    // TCPv6 gönderim
                    case 27: send = false; v6 = true; proto = 6; break;   // TCPv6 alım
                    case 42: send = true; v6 = false; proto = 17; break;  // UDPv4 gönderim
                    case 43: send = false; v6 = false; proto = 17; break; // UDPv4 alım
                    case 58: send = true; v6 = true; proto = 17; break;   // UDPv6 gönderim
                    case 59: send = false; v6 = true; proto = 17; break;  // UDPv6 alım
                    default: return;
                }
                int len = (ushort)Marshal.ReadInt16(rec, 86);
                IntPtr data = Marshal.ReadIntPtr(rec, IntPtr.Size == 8 ? 96 : 92);
                if (data == IntPtr.Zero || len < (v6 ? 44 : 20)) return;

                int pid = Marshal.ReadInt32(data, 0);
                long size = (uint)Marshal.ReadInt32(data, 4);
                var key = new FlowKey { Pid = pid, Proto = proto, V6 = v6 };

                // Uzak uç: yerel adreslerimizden biri olmayan taraf.
                if (!v6)
                {
                    uint daddr = (uint)Marshal.ReadInt32(data, 8);
                    uint saddr = (uint)Marshal.ReadInt32(data, 12);
                    ushort dport = Swap(Marshal.ReadInt16(data, 16));
                    ushort sport = Swap(Marshal.ReadInt16(data, 18));
                    var l4 = Local4;
                    bool dLocal = l4.Contains(daddr), sLocal = l4.Contains(saddr);
                    bool remoteIsDest = dLocal != sLocal ? sLocal : send;
                    uint remote = remoteIsDest ? daddr : saddr;
                    if (remote == 0 || (remote & 0xFF) == 127) return;
                    key.Lo = remote;
                    key.Port = remoteIsDest ? dport : sport;
                }
                else
                {
                    ulong dHi = (ulong)Marshal.ReadInt64(data, 8), dLo = (ulong)Marshal.ReadInt64(data, 16);
                    ulong sHi = (ulong)Marshal.ReadInt64(data, 24), sLo = (ulong)Marshal.ReadInt64(data, 32);
                    ushort dport = Swap(Marshal.ReadInt16(data, 40));
                    ushort sport = Swap(Marshal.ReadInt16(data, 42));
                    var l6 = Local6;
                    bool dLocal = l6.Contains(Hash6(dHi, dLo)), sLocal = l6.Contains(Hash6(sHi, sLo));
                    bool remoteIsDest = dLocal != sLocal ? sLocal : send;
                    key.Hi = remoteIsDest ? dHi : sHi;
                    key.Lo = remoteIsDest ? dLo : sLo;
                    key.Port = remoteIsDest ? dport : sport;
                    if (key.Hi == 0 && (key.Lo == 0 || key.Lo == 0x0100000000000000UL)) return;   // :: veya ::1
                    if (key.Hi == 0 && (key.Lo & 0xFFFFFFFFUL) == 0xFFFF0000UL && ((key.Lo >> 32) & 0xFF) == 127) return;
                }

                Interlocked.Increment(ref eventCount);
                lock (gate)
                {
                    long[] v;
                    if (!pending.TryGetValue(key, out v)) { v = new long[2]; pending[key] = v; }
                    v[send ? 1 : 0] += size;
                }
            }
            catch { }
        }

        // DNS-Client 3008 (sorgu tamamlandı): QueryName, QueryType, QueryOptions, QueryStatus, QueryResults.
        // QueryResults örneği: "type:  5 youtube-ui.l.google.com;::ffff:142.250.1.1;"
        void OnDnsEvent(IntPtr rec)
        {
            var handler = DnsAnswer;
            if (handler == null) return;
            int len = (ushort)Marshal.ReadInt16(rec, 86);
            IntPtr data = Marshal.ReadIntPtr(rec, IntPtr.Size == 8 ? 96 : 92);
            if (data == IntPtr.Zero) return;
            int off = 0;
            string name = ReadWString(data, len, ref off);
            if (string.IsNullOrEmpty(name) || off + 16 > len) return;
            int status = Marshal.ReadInt32(data, off + 12);
            off += 16;
            if (status != 0) return;
            string results = ReadWString(data, len, ref off);
            if (string.IsNullOrEmpty(results)) return;
            name = name.TrimEnd('.');
            foreach (string part in results.Split(';'))
            {
                IPAddress ip;
                if (!IPAddress.TryParse(part.Trim(), out ip)) continue;
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                handler(ip.ToString(), name);
            }
        }

        static string ReadWString(IntPtr data, int len, ref int off)
        {
            var sb = new StringBuilder();
            while (off + 1 < len)
            {
                char c = (char)Marshal.ReadInt16(data, off);
                off += 2;
                if (c == '\0') return sb.ToString();
                sb.Append(c);
            }
            return sb.ToString();
        }

        public Dictionary<FlowKey, long[]> Drain()
        {
            lock (gate)
            {
                var d = pending;
                pending = new Dictionary<FlowKey, long[]>();
                return d;
            }
        }

        public void Dispose()
        {
            if (!running) return;
            running = false;
            CloseTrace(trace);
            StopByName();
        }
    }

    // IP → alan adı: önce Windows DNS önbelleği, bulunamazsa ters DNS (arka planda).
    class NameResolver
    {
        readonly object gate = new object();
        readonly Dictionary<string, string> names = new Dictionary<string, string>();
        readonly Dictionary<string, DateTime> firstAsked = new Dictionary<string, DateTime>();
        readonly HashSet<string> reversePending = new HashSet<string>();
        DateTime lastRefresh = DateTime.MinValue;
        bool refreshing;

        public volatile bool ReverseDns = true;   // internet adresleri için PTR sorgusu (yalnızca DNS)
        public volatile bool LocalNames = false;  // yerel cihaz adları (NetBIOS/mDNS; trafik üretir)

        readonly HashSet<string> fromDns = new HashSet<string>();

        // DNS istemcisinin (ETW) ya da önbelleğinin döndürdüğü bir yanıt.
        public void Add(string ip, string name)
        {
            lock (gate) { names[ip] = name; fromDns.Add(ip); }
        }

        public bool FromDns(string ip)
        {
            lock (gate) { return fromDns.Contains(ip); }
        }

        public string Lookup(string ip)
        {
            DateTime now = DateTime.UtcNow;
            bool startReverse = false;
            lock (gate)
            {
                string n;
                if (names.TryGetValue(ip, out n)) return n.Length > 0 ? n : null;
                DateTime t;
                if (!firstAsked.TryGetValue(ip, out t)) { firstAsked[ip] = now; t = now; }
                if ((now - t).TotalSeconds > 4 && reversePending.Count < 4 && !reversePending.Contains(ip))
                {
                    reversePending.Add(ip);
                    startReverse = true;
                }
            }
            RefreshCache(false);
            if (startReverse) ThreadPool.QueueUserWorkItem(delegate { Reverse(ip); });
            return null;
        }

        public void RefreshCache(bool force)
        {
            lock (gate)
            {
                if (refreshing || (!force && (DateTime.UtcNow - lastRefresh).TotalSeconds < 3)) return;
                refreshing = true;
                lastRefresh = DateTime.UtcNow;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var scope = new ManagementScope(@"root\StandardCimv2");
                    var q = new ObjectQuery("SELECT Entry, Data, Type FROM MSFT_DNSClientCache");
                    using (var s = new ManagementObjectSearcher(scope, q))
                    using (var results = s.Get())
                    {
                        foreach (ManagementObject mo in results)
                        {
                            using (mo)
                            {
                                int type = Convert.ToInt32(mo["Type"]);
                                if (type != 1 && type != 28) continue;
                                string data = mo["Data"] as string, entry = mo["Entry"] as string;
                                if (string.IsNullOrEmpty(data) || string.IsNullOrEmpty(entry)) continue;
                                IPAddress parsed;
                                if (IPAddress.TryParse(data, out parsed)) data = parsed.ToString();
                                Add(data, entry);
                            }
                        }
                    }
                }
                catch { }
                finally { lock (gate) { refreshing = false; } }
            });
        }

        [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, EntryPoint = "DnsQuery_W")]
        static extern int DnsQuery(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);

        [DllImport("dnsapi.dll")]
        static extern void DnsRecordListFree(IntPtr list, int freeType);

        const ushort DNS_TYPE_PTR = 12;
        const uint DNS_QUERY_NO_NETBT = 0x80, DNS_QUERY_NO_MULTICAST = 0x800;

        // Yerel ağ, bağlantı-yerel ve çok noktaya yayın adresleri için ters sorgu yapılmaz:
        // Windows bunları NetBIOS/mDNS ile çözmeye çalışır ve gadget izlediği trafiği kendisi üretir.
        internal static bool IsPublic(IPAddress ip)
        {
            byte[] b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                if (b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] >= 224) return false;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
                if (b[0] == 192 && b[1] == 168) return false;
                if (b[0] == 169 && b[1] == 254) return false;
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;   // CGNAT
                return b[3] != 255;
            }
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || IPAddress.IsLoopback(ip)) return false;
            return (b[0] & 0xFE) != 0xFC;   // fc00::/7 (ULA)
        }

        static string PtrName(IPAddress ip)
        {
            byte[] b = ip.GetAddressBytes();
            var sb = new StringBuilder();
            if (b.Length == 4)
            {
                for (int i = 3; i >= 0; i--) sb.Append(b[i]).Append('.');
                return sb.Append("in-addr.arpa").ToString();
            }
            for (int i = 15; i >= 0; i--)
                sb.Append("0123456789abcdef"[b[i] & 0xF]).Append('.').Append("0123456789abcdef"[b[i] >> 4]).Append('.');
            return sb.Append("ip6.arpa").ToString();
        }

        // Yalnızca DNS üzerinden PTR sorgusu (NetBIOS ve mDNS kapalı).
        static string QueryPtr(IPAddress ip)
        {
            IntPtr results;
            if (DnsQuery(PtrName(ip), DNS_TYPE_PTR, DNS_QUERY_NO_NETBT | DNS_QUERY_NO_MULTICAST, IntPtr.Zero, out results, IntPtr.Zero) != 0)
                return null;
            try
            {
                int dataOffset = IntPtr.Size == 8 ? 32 : 24;
                for (IntPtr r = results; r != IntPtr.Zero; r = Marshal.ReadIntPtr(r, 0))
                {
                    if ((ushort)Marshal.ReadInt16(r, IntPtr.Size * 2) != DNS_TYPE_PTR) continue;
                    string host = Marshal.PtrToStringUni(Marshal.ReadIntPtr(r, dataOffset));
                    if (!string.IsNullOrEmpty(host)) return host.TrimEnd('.');
                }
            }
            finally { DnsRecordListFree(results, 1); }
            return null;
        }

        static bool IsMulticastOrBroadcast(IPAddress ip)
        {
            byte[] b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork) return b[0] >= 224 || b[3] == 255;
            return ip.IsIPv6Multicast;
        }

        // Ayarlar değişince başarısız (boş) sonuçları unut ki yeniden denensin.
        public void ForgetMisses()
        {
            lock (gate)
            {
                var misses = new List<string>();
                foreach (var kv in names) if (kv.Value.Length == 0) misses.Add(kv.Key);
                foreach (var ip in misses) { names.Remove(ip); firstAsked.Remove(ip); }
            }
        }

        void Reverse(string ip)
        {
            string name = "";
            try
            {
                IPAddress parsed;
                if (IPAddress.TryParse(ip, out parsed))
                {
                    if (IsPublic(parsed))
                    {
                        if (ReverseDns) name = QueryPtr(parsed) ?? "";
                    }
                    else if (LocalNames && !IsMulticastOrBroadcast(parsed) && !IPAddress.IsLoopback(parsed))
                    {
                        // Windows'un tam çözümlemesi: DNS, NetBIOS ve mDNS (yerel ağda trafik üretir).
                        string h = Dns.GetHostEntry(parsed).HostName;
                        if (!string.IsNullOrEmpty(h) && h != ip) name = h;
                    }
                }
            }
            catch { }
            lock (gate)
            {
                string existing;
                if (!names.TryGetValue(ip, out existing) || existing.Length == 0) names[ip] = name;
                reversePending.Remove(ip);
            }
        }
    }

    public class Sampler : IDisposable
    {
        const int AF_INET = 2;
        const int AF_INET6 = 23;
        const int TCP_TABLE_OWNER_PID_ALL = 5;
        const int MIB_TCP_STATE_ESTAB = 5;
        const int TcpConnectionEstatsData = 1;
        const uint ERROR_ACCESS_DENIED = 5;
        const uint ERROR_INSUFFICIENT_BUFFER = 122;
        const double LingerSeconds = 30;
        const int MaxEndpointsPerApp = 15;
        const int MaxDiscoveryPerApp = 30;

        [StructLayout(LayoutKind.Sequential)]
        struct MIB_TCPROW
        {
            public uint dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MIB_TCP6ROW
        {
            public uint State;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
            public uint dwLocalScopeId, dwLocalPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
            public uint dwRemoteScopeId, dwRemotePort;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TCP_ESTATS_DATA_ROD_v0
        {
            public ulong DataBytesOut, DataSegsOut, DataBytesIn, DataSegsIn, SegsOut, SegsIn;
            public uint SoftErrors, SoftErrorReason, SndUna, SndNxt, SndMax;
            public ulong ThruBytesAcked;
            public uint RcvNxt;
            public ulong ThruBytesReceived;
        }

        [DllImport("iphlpapi.dll")]
        static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int TableClass, uint Reserved);

        [DllImport("iphlpapi.dll")]
        static extern uint SetPerTcpConnectionEStats(ref MIB_TCPROW Row, int EstatsType, IntPtr Rw, uint RwVersion, uint RwSize, uint Offset);

        [DllImport("iphlpapi.dll")]
        static extern uint GetPerTcpConnectionEStats(ref MIB_TCPROW Row, int EstatsType, IntPtr Rw, uint RwVersion, uint RwSize,
            IntPtr Ros, uint RosVersion, uint RosSize, IntPtr Rod, uint RodVersion, uint RodSize);

        [DllImport("iphlpapi.dll")]
        static extern uint SetPerTcp6ConnectionEStats(ref MIB_TCP6ROW Row, int EstatsType, IntPtr Rw, uint RwVersion, uint RwSize, uint Offset);

        [DllImport("iphlpapi.dll")]
        static extern uint GetPerTcp6ConnectionEStats(ref MIB_TCP6ROW Row, int EstatsType, IntPtr Rw, uint RwVersion, uint RwSize,
            IntPtr Ros, uint RosVersion, uint RosSize, IntPtr Rod, uint RodVersion, uint RodSize);

        [DllImport("kernel32.dll")]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr hObject);

        readonly int rodSize = Marshal.SizeOf(typeof(TCP_ESTATS_DATA_ROD_v0));
        readonly IntPtr rod;
        readonly IntPtr rwEnable;
        readonly EtwNet etw;
        readonly NameResolver resolver = new NameResolver();

        readonly Dictionary<FlowKey, Flow> flows = new Dictionary<FlowKey, Flow>();
        readonly Dictionary<string, long[]> appTotals = new Dictionary<string, long[]>();
        Dictionary<string, ulong[]> prevEStats = new Dictionary<string, ulong[]>();
        readonly HashSet<string> enabled = new HashSet<string>();
        readonly Dictionary<int, ProcEntry> procCache = new Dictionary<int, ProcEntry>();
        readonly object svcGate = new object();
        readonly Dictionary<int, string[]> svcCache = new Dictionary<int, string[]>();
        readonly HashSet<int> svcPending = new HashSet<int>();
        DateTime last = DateTime.MinValue;
        long lastIfIn = -1, lastIfOut = -1;
        int sampleCount;
        readonly DateTime started = DateTime.UtcNow;
        readonly Inspector inspector;
        readonly HashSet<long> listenPorts = new HashSet<long>();              // (pid << 16) | yerel port
        readonly Dictionary<int, List<string>> listening = new Dictionary<int, List<string>>();

        public Inspector Inspector { get { return inspector; } }
        public bool EtwActive { get; private set; }
        public string EtwError { get; private set; }
        public bool EStatsDenied { get; private set; }
        public double TotalIn { get; private set; }
        public double TotalOut { get; private set; }
        public int TcpCount { get; private set; }
        public int UdpCount { get; private set; }
        public int LocalCount { get; private set; }
        public long EventCount { get { return etw != null ? etw.EventCount : 0; } }

        public bool ReverseDns
        {
            get { return resolver.ReverseDns; }
            set { if (resolver.ReverseDns != value) { resolver.ReverseDns = value; resolver.ForgetMisses(); } }
        }

        public bool LocalNames
        {
            get { return resolver.LocalNames; }
            set { if (resolver.LocalNames != value) { resolver.LocalNames = value; resolver.ForgetMisses(); } }
        }

        // dataDir: "ilk kez görüldü" ve güvenilir listesi dosyalarının klasörü (null: yalnızca bellekte).
        public Sampler(bool useEtw, string dataDir)
        {
            inspector = new Inspector(dataDir);
            rod = Marshal.AllocHGlobal(rodSize);
            rwEnable = Marshal.AllocHGlobal(1);
            Marshal.WriteByte(rwEnable, 1);

            if (useEtw)
            {
                var e = new EtwNet();
                e.DnsAnswer = resolver.Add;
                RefreshLocalAddresses(e);
                if (e.Start())
                {
                    etw = e;
                    EtwActive = true;
                    AppDomain.CurrentDomain.ProcessExit += delegate { e.Dispose(); };
                }
                else EtwError = e.Error;
            }
            resolver.RefreshCache(true);
        }

        public void Dispose()
        {
            if (etw != null) etw.Dispose();
            inspector.Dispose();
        }

        public List<AppStat> Sample()
        {
            DateTime now = DateTime.UtcNow;
            double secs = last == DateTime.MinValue ? 0 : (now - last).TotalSeconds;
            last = now;
            sampleCount++;
            if (sampleCount % 120 == 0) procCache.Clear();
            if (etw != null && sampleCount % 10 == 0) RefreshLocalAddresses(etw);

            foreach (var f in flows.Values) { f.TickIn = 0; f.TickOut = 0; f.Open = false; f.Conns = 0; }

            if (etw != null)
            {
                foreach (var kv in etw.Drain())
                {
                    Flow f = GetFlow(kv.Key, now);
                    f.TickIn += kv.Value[0];
                    f.TickOut += kv.Value[1];
                    f.TotalIn += kv.Value[0];
                    f.TotalOut += kv.Value[1];
                    f.LastSeen = now;
                }
            }

            var seen = new HashSet<string>();
            var cur = new Dictionary<string, ulong[]>();
            listenPorts.Clear();
            listening.Clear();
            ReadListeners(AF_INET);
            ReadListeners(AF_INET6);
            ReadV4(now, seen, cur);
            ReadV6(now, seen, cur);
            if (etw == null)
            {
                prevEStats = cur;
                enabled.IntersectWith(seen);
            }
            SampleInterfaces(secs);

            var dead = new List<FlowKey>();
            foreach (var kv in flows)
            {
                Flow f = kv.Value;
                if (f.Open) continue;
                if (f.TotalIn + f.TotalOut == 0 || (now - f.LastSeen).TotalSeconds > LingerSeconds) dead.Add(kv.Key);
            }
            foreach (var k in dead) flows.Remove(k);

            return BuildApps(secs);
        }

        List<AppStat> BuildApps(double secs)
        {
            var apps = new Dictionary<string, AppStat>();
            var eps = new Dictionary<string, EndpointStat>();
            foreach (var kv in flows)
            {
                FlowKey k = kv.Key;
                Flow f = kv.Value;
                ProcEntry pe = ProcInfo(k.Pid);
                AppStat app;
                if (!apps.TryGetValue(pe.Key, out app))
                {
                    app = new AppStat { Key = pe.Key, Name = pe.Name, Path = pe.Path };
                    if (pe.IsSvchost)
                    {
                        string[] svc = ServiceNames(pe.Pid);
                        if (svc != null && svc.Length > 0)
                        {
                            app.Name = svc[0] + (svc.Length > 1 ? " +" + (svc.Length - 1) : "");
                            app.Services = string.Join(", ", svc);
                        }
                        else app.Name = "Windows hizmeti (svchost)";
                    }
                    apps[pe.Key] = app;
                }
                if (!app.Pids.Contains(k.Pid)) app.Pids.Add(k.Pid);

                double inR = secs > 0 ? f.TickIn / secs : 0;
                double outR = secs > 0 ? f.TickOut / secs : 0;
                app.InRate += inR;
                app.OutRate += outR;

                long[] tot;
                if (!appTotals.TryGetValue(app.Key, out tot)) { tot = new long[2]; appTotals[app.Key] = tot; }
                tot[0] += f.TickIn;
                tot[1] += f.TickOut;

                string proto = k.Proto == 6 ? "TCP" : "UDP";
                string ek = app.Key + "|" + proto + "|" + f.Ip + "|" + k.Port;
                EndpointStat ep;
                if (!eps.TryGetValue(ek, out ep))
                {
                    ep = new EndpointStat
                    {
                        Protocol = proto, Address = f.Ip, Port = k.Port, LastSeen = f.LastSeen,
                        Service = PortName(k.Port, k.Proto), Host = resolver.Lookup(f.Ip),
                        Multicast = IsMulticast(k), Public = f.Public, FirstSeen = f.FirstSeen
                    };
                    ep.Discovery = ep.Multicast || IsDiscoveryPort(k);
                    // ETW'nin DNS yanıtlarını görmeye başlamasından önce açılmış bağlantılar için karar verilemez.
                    ep.NoDns = etw != null && f.Public && !ep.Discovery
                        && (f.FirstSeen - started).TotalSeconds > 60 && !resolver.FromDns(f.Ip);
                    eps[ek] = ep;
                    (ep.Discovery ? app.Discovery : app.Endpoints).Add(ep);
                }
                ep.InRate += inR;
                ep.OutRate += outR;
                ep.TotalIn += f.TotalIn;
                ep.TotalOut += f.TotalOut;
                ep.Open |= f.Open;
                ep.Inbound |= f.Inbound;
                ep.Count += f.Conns;
                if (f.LastSeen > ep.LastSeen) ep.LastSeen = f.LastSeen;
                if (f.FirstSeen < ep.FirstSeen) ep.FirstSeen = f.FirstSeen;
            }

            TcpCount = 0;
            UdpCount = 0;
            LocalCount = 0;
            var list = new List<AppStat>(apps.Values);
            foreach (var app in list)
            {
                foreach (var ep in app.Endpoints)
                {
                    if (ep.Protocol == "TCP") { if (ep.Open) app.TcpCount += Math.Max(1, ep.Count); }
                    else app.UdpCount++;
                    if (ep.Inbound) ep.NoDns = false;   // gelen bağlantı için DNS sorgusu beklenmez
                }
                foreach (int pid in app.Pids)
                {
                    List<string> l;
                    if (!listening.TryGetValue(pid, out l)) continue;
                    foreach (string s in l)
                    {
                        if (app.Listening.Contains(s)) continue;
                        app.Listening.Add(s);
                        if (s.StartsWith("0.0.0.0:") || s.StartsWith("[::]:")) app.ListensAll = true;
                    }
                }
                var devices = new HashSet<string>();
                foreach (var ep in app.Discovery) if (!ep.Multicast) devices.Add(ep.Address);
                app.LocalDevices = devices.Count;
                TcpCount += app.TcpCount;
                UdpCount += app.UdpCount;
                LocalCount += app.Discovery.Count;
                long[] tot = appTotals[app.Key];
                app.TotalIn = tot[0];
                app.TotalOut = tot[1];
                app.Endpoints.Sort(CompareEndpoints);
                app.Discovery.Sort(CompareEndpoints);
                if (app.Endpoints.Count > MaxEndpointsPerApp)
                    app.Endpoints.RemoveRange(MaxEndpointsPerApp, app.Endpoints.Count - MaxEndpointsPerApp);
                if (app.Discovery.Count > MaxDiscoveryPerApp)
                    app.Discovery.RemoveRange(MaxDiscoveryPerApp, app.Discovery.Count - MaxDiscoveryPerApp);
                inspector.Evaluate(app);
            }

            list.Sort(delegate(AppStat a, AppStat b)
            {
                int c = b.Total.CompareTo(a.Total);
                if (c != 0) return c;
                c = b.Connections.CompareTo(a.Connections);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return list;
        }

        static int CompareEndpoints(EndpointStat a, EndpointStat b)
        {
            int c = b.Total.CompareTo(a.Total);
            if (c != 0) return c;
            c = b.Open.CompareTo(a.Open);
            if (c != 0) return c;
            c = b.LastSeen.CompareTo(a.LastSeen);
            return c != 0 ? c : (b.TotalIn + b.TotalOut).CompareTo(a.TotalIn + a.TotalOut);
        }

        static bool IsMulticast(FlowKey k)
        {
            if (!k.V6)
            {
                uint b0 = (uint)(k.Lo & 0xFF);
                return (b0 >= 224 && b0 <= 239) || (k.Lo & 0xFFFFFFFF) == 0xFFFFFFFF;
            }
            return (k.Hi & 0xFF) == 0xFF;   // ff00::/8
        }

        static bool IsDiscoveryPort(FlowKey k)
        {
            if (k.Proto != 17) return false;
            switch (k.Port)
            {
                case 5353: case 5355: case 1900: case 3702: case 137: case 138:
                case 67: case 68: case 546: case 547:
                    return true;
            }
            return false;
        }

        Flow GetFlow(FlowKey key, DateTime now)
        {
            Flow f;
            if (!flows.TryGetValue(key, out f))
            {
                string ip = key.AddressText();
                f = new Flow { Ip = ip, LastSeen = now, FirstSeen = now, Public = NameResolver.IsPublic(IPAddress.Parse(ip)) };
                flows[key] = f;
            }
            return f;
        }

        static void RefreshLocalAddresses(EtwNet e)
        {
            var v4 = new HashSet<uint>();
            var v6 = new HashSet<ulong>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        byte[] b = ua.Address.GetAddressBytes();
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork) v4.Add(BitConverter.ToUInt32(b, 0));
                        else if (b.Length == 16) v6.Add(EtwNet.Hash6(BitConverter.ToUInt64(b, 0), BitConverter.ToUInt64(b, 8)));
                    }
                }
            }
            catch { return; }
            e.Local4 = v4;
            e.Local6 = v6;
        }

        IntPtr ReadTable(int af)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                size += 4096;
                IntPtr buf = Marshal.AllocHGlobal(size);
                uint r = GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
                if (r == 0) return buf;
                Marshal.FreeHGlobal(buf);
                if (r != ERROR_INSUFFICIENT_BUFFER) break;
            }
            return IntPtr.Zero;
        }

        // Bağlantı bekleyen (LISTEN) TCP soketleri: gelen bağlantıları ayırt etmek ve
        // "tüm arayüzlerde dinliyor" bilgisini göstermek için. Loopback dinleyiciler atlanır.
        void ReadListeners(int af)
        {
            IntPtr buf = ReadTable(af);
            if (buf == IntPtr.Zero) return;
            try
            {
                int n = Marshal.ReadInt32(buf);
                int rowSize = af == AF_INET ? 24 : 56;
                for (int i = 0; i < n; i++)
                {
                    IntPtr p = new IntPtr(buf.ToInt64() + 4 + i * rowSize);
                    string text;
                    int pid, port;
                    if (af == AF_INET)
                    {
                        if (Marshal.ReadInt32(p, 0) != 2 /* LISTEN */) continue;
                        uint la = (uint)Marshal.ReadInt32(p, 4);
                        if ((la & 0xFF) == 127) continue;
                        port = Port((uint)Marshal.ReadInt32(p, 8));
                        pid = Marshal.ReadInt32(p, 20);
                        text = new IPAddress((long)la) + ":" + port;
                    }
                    else
                    {
                        if (Marshal.ReadInt32(p, 48) != 2) continue;
                        var la = new byte[16];
                        Marshal.Copy(p, la, 0, 16);
                        var ip = new IPAddress(la);
                        if (IPAddress.IsLoopback(ip)) continue;
                        port = Port((uint)Marshal.ReadInt32(p, 20));
                        pid = Marshal.ReadInt32(p, 52);
                        text = "[" + ip + "]:" + port;
                    }
                    listenPorts.Add(ListenKey(pid, port));
                    List<string> l;
                    if (!listening.TryGetValue(pid, out l)) { l = new List<string>(); listening[pid] = l; }
                    if (!l.Contains(text)) l.Add(text);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        void ReadV4(DateTime now, HashSet<string> seen, Dictionary<string, ulong[]> cur)
        {
            IntPtr buf = ReadTable(AF_INET);
            if (buf == IntPtr.Zero) return;
            try
            {
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++)
                {
                    IntPtr p = new IntPtr(buf.ToInt64() + 4 + i * 24);
                    uint state = (uint)Marshal.ReadInt32(p, 0);
                    if (state != MIB_TCP_STATE_ESTAB) continue;
                    uint la = (uint)Marshal.ReadInt32(p, 4);
                    uint lp = (uint)Marshal.ReadInt32(p, 8);
                    uint ra = (uint)Marshal.ReadInt32(p, 12);
                    uint rp = (uint)Marshal.ReadInt32(p, 16);
                    int pid = Marshal.ReadInt32(p, 20);
                    if (ra == 0 || (ra & 0xFF) == 127) continue;

                    Flow f = GetFlow(new FlowKey { Pid = pid, Proto = 6, Lo = ra, Port = (ushort)Port(rp) }, now);
                    f.Open = true;
                    f.Conns++;
                    f.LastSeen = now;
                    if (listenPorts.Contains(ListenKey(pid, Port(lp)))) f.Inbound = true;
                    if (etw != null || EStatsDenied) continue;

                    string key = "4|" + la + ":" + lp + "|" + ra + ":" + rp + "|" + pid;
                    seen.Add(key);
                    var row = new MIB_TCPROW { dwState = state, dwLocalAddr = la, dwLocalPort = lp, dwRemoteAddr = ra, dwRemotePort = rp };
                    if (!enabled.Contains(key))
                    {
                        uint r = SetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData, rwEnable, 0, 1, 0);
                        if (r == ERROR_ACCESS_DENIED) { EStatsDenied = true; continue; }
                        if (r != 0) continue;
                        enabled.Add(key);
                    }
                    uint g = GetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData, IntPtr.Zero, 0, 0,
                        IntPtr.Zero, 0, 0, rod, 0, (uint)rodSize);
                    if (g == 0) AddEStats(f, key, cur);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        void ReadV6(DateTime now, HashSet<string> seen, Dictionary<string, ulong[]> cur)
        {
            IntPtr buf = ReadTable(AF_INET6);
            if (buf == IntPtr.Zero) return;
            try
            {
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++)
                {
                    IntPtr p = new IntPtr(buf.ToInt64() + 4 + i * 56);
                    uint state = (uint)Marshal.ReadInt32(p, 48);
                    if (state != MIB_TCP_STATE_ESTAB) continue;

                    var la = new byte[16];
                    var ra = new byte[16];
                    Marshal.Copy(p, la, 0, 16);
                    Marshal.Copy(new IntPtr(p.ToInt64() + 24), ra, 0, 16);
                    uint lscope = (uint)Marshal.ReadInt32(p, 16);
                    uint lp = (uint)Marshal.ReadInt32(p, 20);
                    uint rscope = (uint)Marshal.ReadInt32(p, 40);
                    uint rp = (uint)Marshal.ReadInt32(p, 44);
                    int pid = Marshal.ReadInt32(p, 52);

                    var remote = new IPAddress(ra);
                    if (remote.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(remote)) continue;
                    if (remote.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remote.MapToIPv4())) continue;

                    var fk = new FlowKey
                    {
                        Pid = pid, Proto = 6, V6 = true, Port = (ushort)Port(rp),
                        Hi = BitConverter.ToUInt64(ra, 0), Lo = BitConverter.ToUInt64(ra, 8)
                    };
                    Flow f = GetFlow(fk, now);
                    f.Open = true;
                    f.Conns++;
                    f.LastSeen = now;
                    if (listenPorts.Contains(ListenKey(pid, Port(lp)))) f.Inbound = true;
                    if (etw != null || EStatsDenied) continue;

                    string key = "6|" + BitConverter.ToString(la) + ":" + lp + "|" + BitConverter.ToString(ra) + ":" + rp + "|" + pid;
                    seen.Add(key);
                    var row = new MIB_TCP6ROW
                    {
                        State = state, LocalAddr = la, dwLocalScopeId = lscope, dwLocalPort = lp,
                        RemoteAddr = ra, dwRemoteScopeId = rscope, dwRemotePort = rp
                    };
                    if (!enabled.Contains(key))
                    {
                        uint r = SetPerTcp6ConnectionEStats(ref row, TcpConnectionEstatsData, rwEnable, 0, 1, 0);
                        if (r == ERROR_ACCESS_DENIED) { EStatsDenied = true; continue; }
                        if (r != 0) continue;
                        enabled.Add(key);
                    }
                    uint g = GetPerTcp6ConnectionEStats(ref row, TcpConnectionEstatsData, IntPtr.Zero, 0, 0,
                        IntPtr.Zero, 0, 0, rod, 0, (uint)rodSize);
                    if (g == 0) AddEStats(f, key, cur);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        void AddEStats(Flow f, string key, Dictionary<string, ulong[]> cur)
        {
            var d = (TCP_ESTATS_DATA_ROD_v0)Marshal.PtrToStructure(rod, typeof(TCP_ESTATS_DATA_ROD_v0));
            cur[key] = new ulong[] { d.DataBytesIn, d.DataBytesOut };
            ulong[] pv;
            if (!prevEStats.TryGetValue(key, out pv)) return;
            long din = d.DataBytesIn >= pv[0] ? (long)(d.DataBytesIn - pv[0]) : 0;
            long dout = d.DataBytesOut >= pv[1] ? (long)(d.DataBytesOut - pv[1]) : 0;
            f.TickIn += din;
            f.TickOut += dout;
            f.TotalIn += din;
            f.TotalOut += dout;
        }

        ProcEntry ProcInfo(int pid)
        {
            ProcEntry v;
            if (procCache.TryGetValue(pid, out v)) return v;

            string name = null, path = null;
            if (pid == 0) name = "System Idle";
            else if (pid == 4) name = "System";
            else
            {
                IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
                if (h != IntPtr.Zero)
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
                    CloseHandle(h);
                }
                if (path != null)
                {
                    try
                    {
                        string desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                        if (!string.IsNullOrWhiteSpace(desc)) name = desc.Trim();
                    }
                    catch { }
                    if (name == null) name = System.IO.Path.GetFileNameWithoutExtension(path);
                }
                else
                {
                    try { name = Process.GetProcessById(pid).ProcessName; }
                    catch { name = "PID " + pid; }
                }
            }

            bool svchost = (path != null && path.EndsWith("\\svchost.exe", StringComparison.OrdinalIgnoreCase))
                || (path == null && string.Equals(name, "svchost", StringComparison.OrdinalIgnoreCase));
            v = new ProcEntry
            {
                Pid = pid, Name = name, Path = path, IsSvchost = svchost,
                Key = svchost ? "svc:" + pid : (path != null ? path.ToLowerInvariant() : "name:" + name)
            };
            procCache[pid] = v;
            return v;
        }

        // svchost süreçlerinin barındırdığı hizmetlerin adları (WMI, arka planda).
        string[] ServiceNames(int pid)
        {
            lock (svcGate)
            {
                string[] v;
                if (svcCache.TryGetValue(pid, out v)) return v;
                if (!svcPending.Add(pid)) return null;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                var list = new List<string>();
                try
                {
                    using (var s = new ManagementObjectSearcher("SELECT DisplayName FROM Win32_Service WHERE ProcessId = " + pid))
                    using (var results = s.Get())
                    {
                        foreach (ManagementObject mo in results)
                        {
                            using (mo)
                            {
                                string d = mo["DisplayName"] as string;
                                if (!string.IsNullOrEmpty(d)) list.Add(d);
                            }
                        }
                    }
                }
                catch { }
                list.Sort(StringComparer.CurrentCultureIgnoreCase);
                lock (svcGate)
                {
                    svcCache[pid] = list.ToArray();
                    svcPending.Remove(pid);
                }
            });
            return null;
        }

        void SampleInterfaces(double secs)
        {
            long tin = 0, tout = 0;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var s = ni.GetIPStatistics();
                    tin += s.BytesReceived;
                    tout += s.BytesSent;
                }
            }
            catch { return; }

            if (secs > 0 && lastIfIn >= 0)
            {
                TotalIn = Math.Max(0, tin - lastIfIn) / secs;
                TotalOut = Math.Max(0, tout - lastIfOut) / secs;
            }
            lastIfIn = tin;
            lastIfOut = tout;
        }

        static long ListenKey(int pid, int port)
        {
            return ((long)pid << 16) | (uint)(port & 0xFFFF);
        }

        static int Port(uint raw)
        {
            return (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
        }

        public static string PortName(int port, byte proto)
        {
            bool udp = proto == 17;
            switch (port)
            {
                case 443: return udp ? "QUIC" : "HTTPS";
                case 80: return "HTTP";
                case 8080: case 8443: return "HTTP-alt";
                case 53: return "DNS";
                case 853: return "DNS-TLS";
                case 123: return "NTP";
                case 5353: return "mDNS";
                case 5355: return "LLMNR";
                case 3702: return "WS-Discovery";
                case 1900: return "SSDP";
                case 137: case 138: case 139: return "NetBIOS";
                case 445: return "SMB";
                case 67: case 68: return "DHCP";
                case 546: case 547: return "DHCPv6";
                case 22: return "SSH";
                case 3389: return "RDP";
                case 5222: case 5223: return "XMPP/Push";
                case 5228: return "Google Push";
                case 993: return "IMAPS";
                case 995: return "POP3S";
                case 25: case 465: case 587: return "SMTP";
                case 3478: case 3479: case 3480: case 3481: case 19302: return "STUN/TURN";
                case 500: case 4500: return "IPsec";
                case 1194: return "OpenVPN";
                case 51820: return "WireGuard";
                case 7680: return "Teslim İyileştirme";
            }
            if (port >= 27000 && port <= 27100) return "Steam";
            return null;
        }

        public static string FormatRate(double bytesPerSec)
        {
            if (bytesPerSec < 1024) return bytesPerSec.ToString("0") + " B/s";
            double kb = bytesPerSec / 1024;
            if (kb < 1024) return kb.ToString(kb < 10 ? "0.0" : "0") + " KB/s";
            double mb = kb / 1024;
            return mb.ToString(mb < 100 ? "0.0" : "0") + " MB/s";
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString(kb < 10 ? "0.0" : "0") + " KB";
            double mb = kb / 1024;
            if (mb < 1024) return mb.ToString(mb < 10 ? "0.0" : "0") + " MB";
            return (mb / 1024).ToString("0.00") + " GB";
        }
    }
}
