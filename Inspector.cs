// Şüpheli bağlantı incelemesi: süreç kimliği (imza, konum, ad, komut satırı, üst süreç),
// uç nokta işaretleri (DNS'siz IP, gelen bağlantı, şüpheli port), "ilk kez görüldü"
// kayıtları ve güvenilir listesi. Hiçbir kontrol internete çıkmaz.
// PowerShell 5.1 Add-Type (C# 5) ile derlenir: C# 6+ sözdizimi kullanmayın.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace NetGadget
{
    public class ProcDetails
    {
        public string CommandLine;
        public int ParentPid;
        public string ParentName;
        public string User;
        public DateTime StartTime;
    }

    public class SignatureInfo
    {
        public string Status;   // Valid, NotSigned, Invalid, Unknown
        public string Detail;   // Get-AuthenticodeSignature durumu
        public string Signer;
    }

    // Basit "anahtar<TAB>ilk görülme" dosyası; dosya yolu verilmezse yalnızca bellekte tutar.
    class KeyStore
    {
        readonly string file;
        readonly Dictionary<string, DateTime> map = new Dictionary<string, DateTime>();
        public readonly bool Existed;

        public KeyStore(string file)
        {
            this.file = file;
            if (file == null || !File.Exists(file)) return;
            Existed = true;
            try
            {
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    int tab = line.LastIndexOf('\t');
                    long ticks;
                    if (tab > 0 && long.TryParse(line.Substring(tab + 1), out ticks))
                        map[line.Substring(0, tab)] = new DateTime(ticks, DateTimeKind.Utc);
                }
            }
            catch { }
        }

        public bool TryGet(string key, out DateTime first) { return map.TryGetValue(key, out first); }
        public bool Contains(string key) { return map.ContainsKey(key); }

        public void Add(string key, DateTime when)
        {
            key = Clean(key);
            if (map.ContainsKey(key)) return;
            map[key] = when;
            if (file == null) return;
            try { File.AppendAllText(file, key + "\t" + when.Ticks + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }

        public void Remove(string key)
        {
            if (!map.Remove(Clean(key)) || file == null) return;
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in map) sb.Append(kv.Key).Append('\t').Append(kv.Value.Ticks).AppendLine();
                File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        static string Clean(string key) { return key.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }
    }

    public class Inspector : IDisposable
    {
        static readonly string[] SystemNames =
        {
            "svchost", "lsass", "csrss", "winlogon", "services", "smss", "wininit", "spoolsv", "taskhostw",
            "dllhost", "conhost", "rundll32", "explorer", "searchindexer", "sihost", "ctfmon", "runtimebroker"
        };

        static readonly HashSet<string> Lolbins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "powershell", "pwsh", "cmd", "rundll32", "regsvr32", "mshta", "wscript", "cscript", "certutil",
            "bitsadmin", "msbuild", "installutil", "regasm", "regsvcs", "wmic", "curl", "hh", "forfiles",
            "mavinject", "odbcconf", "msxsl", "cmstp"
        };

        static readonly HashSet<string> OfficeApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "winword", "excel", "powerpnt", "outlook", "msaccess", "mspub", "onenote", "acrord32", "acrobat"
        };

        static readonly string[] CmdTokens =
        {
            "-enc", "-encodedcommand", "frombase64string", "downloadstring", "downloadfile", "invoke-expression",
            "iex(", "iex (", "-w hidden", "-windowstyle hidden", "invoke-webrequest", "net.webclient",
            "start-bitstransfer", "javascript:", "vbscript:", "http://", "https://", "-urlcache"
        };

        static readonly TimeSpan NewWindow = TimeSpan.FromMinutes(10);
        static readonly TimeSpan LearningTime = TimeSpan.FromMinutes(5);

        readonly KeyStore known;
        readonly KeyStore trust;
        readonly DateTime learningUntil;

        readonly object gate = new object();
        readonly Dictionary<int, ProcDetails> procCache = new Dictionary<int, ProcDetails>();
        readonly HashSet<int> procPending = new HashSet<int>();
        readonly Dictionary<string, SignatureInfo> sigCache = new Dictionary<string, SignatureInfo>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> sigPending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Queue<string> sigQueue = new Queue<string>();
        readonly AutoResetEvent sigSignal = new AutoResetEvent(false);
        volatile bool disposed;

        // dataDir null ise kayıtlar yalnızca bellekte tutulur (test için).
        public Inspector(string dataDir)
        {
            string knownFile = null, trustFile = null;
            if (dataDir != null)
            {
                try
                {
                    Directory.CreateDirectory(dataDir);
                    knownFile = Path.Combine(dataDir, "known.tsv");
                    trustFile = Path.Combine(dataDir, "trusted.tsv");
                }
                catch { }
            }
            known = new KeyStore(knownFile);
            trust = new KeyStore(trustFile);
            learningUntil = known.Existed ? DateTime.MinValue : DateTime.UtcNow + LearningTime;

            var t = new Thread(SignatureWorker);
            t.IsBackground = true;
            t.Name = "NetGadget imza";
            t.Start();
        }

        public TimeSpan LearningRemaining
        {
            get
            {
                var r = learningUntil - DateTime.UtcNow;
                return r > TimeSpan.Zero ? r : TimeSpan.Zero;
            }
        }

        public void Dispose()
        {
            disposed = true;
            sigSignal.Set();
        }

        // ---------- Güvenilir listesi ----------

        public void SetAppTrust(AppStat app, bool trusted)
        {
            if (app.StoreKey == null) return;
            if (trusted) trust.Add("A|" + app.StoreKey, DateTime.UtcNow);
            else trust.Remove("A|" + app.StoreKey);
            Evaluate(app);
        }

        public void SetEndpointTrust(AppStat app, EndpointStat ep, bool trusted)
        {
            if (app.StoreKey == null || ep.DomainKey == null) return;
            string key = "E|" + app.StoreKey + "|" + ep.DomainKey;
            if (trusted) trust.Add(key, DateTime.UtcNow);
            else trust.Remove(key);
            Evaluate(app);
        }

        // ---------- Değerlendirme ----------

        public void Evaluate(AppStat app)
        {
            DateTime now = DateTime.UtcNow;
            app.Reasons.Clear();
            int score = 0;
            app.StoreKey = StoreKey(app);
            app.Trusted = app.StoreKey != null && trust.Contains("A|" + app.StoreKey);

            ProcDetails pd = app.Pids.Count > 0 ? GetProc(app.Pids[0]) : null;
            if (pd != null)
            {
                app.CommandLine = pd.CommandLine;
                app.ParentPid = pd.ParentPid;
                app.ParentName = pd.ParentName;
                app.User = pd.User;
                app.StartTime = pd.StartTime;
            }

            string exe = app.Path != null ? Path.GetFileNameWithoutExtension(app.Path).ToLowerInvariant() : null;
            SignatureInfo si = app.Path != null ? GetSignature(app.Path) : null;
            if (si != null)
            {
                app.SignStatus = si.Status;
                app.Signer = si.Signer;
                if (si.Status == "NotSigned") score += Reason(app, 2, Loc.T("reason.unsigned"));
                else if (si.Status == "Invalid") score += Reason(app, 4, Loc.T("reason.badsig", si.Detail));
            }
            if (app.Path != null)
            {
                score += LocationScore(app);
                score += NameScore(app, exe);
            }

            if (exe != null && Lolbins.Contains(exe))
            {
                score += Reason(app, 2, Loc.T("reason.lolbin", exe));
                var found = new List<string>();
                foreach (int pid in app.Pids)
                {
                    ProcDetails p = GetProc(pid);
                    if (p == null || p.CommandLine == null) continue;
                    string cl = p.CommandLine.ToLowerInvariant();
                    foreach (string tok in CmdTokens)
                        if (cl.Contains(tok) && !found.Contains(tok.Trim())) found.Add(tok.Trim());
                }
                if (found.Count > 0) score += Reason(app, 3, Loc.T("reason.cmdline", string.Join(", ", found.ToArray())));
                if (pd != null && pd.ParentName != null && OfficeApps.Contains(pd.ParentName))
                    score += Reason(app, 3, Loc.T("reason.office", pd.ParentName));
            }

            bool noDns = false;
            int inboundPublic = 0;
            var oddPorts = new List<string>();
            foreach (var ep in app.Endpoints)
            {
                ep.Flags.Clear();
                ep.DomainKey = DomainKey(ep);
                ep.Trusted = app.StoreKey != null && trust.Contains("E|" + app.StoreKey + "|" + ep.DomainKey);
                string odd = OddPort(ep.Port);
                ep.Risky = !ep.Trusted && (odd != null || ep.NoDns || (ep.Inbound && ep.Public));
                if (ep.Inbound) ep.Flags.Add(Loc.T(ep.Public ? "flag.inbound.public" : "flag.inbound.local"));
                if (odd != null) ep.Flags.Add(Loc.T("flag.oddport", odd));
                if (ep.NoDns) ep.Flags.Add(Loc.T("flag.nodns"));
                if (app.StoreKey != null && (!string.IsNullOrEmpty(ep.Host) || (now - ep.FirstSeen).TotalSeconds > 15))
                {
                    ep.IsNew = IsNew(Remember("E|" + app.StoreKey + "|" + ep.DomainKey, now), now);
                    if (ep.IsNew) ep.Flags.Add(Loc.T("flag.new"));
                }
                if (ep.Trusted) { ep.Flags.Add(Loc.T("flag.trusted")); continue; }
                if (ep.NoDns) noDns = true;
                if (odd != null && !oddPorts.Contains(ep.Port + " " + odd)) oddPorts.Add(ep.Port + " " + odd);
                if (ep.Inbound && ep.Public) inboundPublic++;
            }
            if (noDns) score += Reason(app, 1, Loc.T("reason.nodns"));
            if (oddPorts.Count > 0) score += Reason(app, 2, Loc.T("reason.oddport", string.Join(", ", oddPorts.ToArray())));
            if (inboundPublic > 0) score += Reason(app, 2, Loc.T("reason.inbound", inboundPublic));
            if (app.ListensAll && app.Path != null && app.SignStatus != null && app.SignStatus != "Valid")
                score += Reason(app, 1, Loc.T("reason.listensall"));

            if (app.StoreKey != null)
            {
                app.IsNew = IsNew(Remember("A|" + app.StoreKey, now), now);
                if (app.IsNew) score += Reason(app, 1, Loc.T("reason.new"));
            }

            if (app.Trusted)
            {
                app.Score = 0;
                app.Level = 0;
            }
            else
            {
                app.Score = score;
                app.Level = score >= 5 ? 3 : score >= 3 ? 2 : score >= 1 ? 1 : 0;
            }
        }

        static int Reason(AppStat app, int weight, string text)
        {
            app.Reasons.Add("+" + weight + "  " + text);
            return weight;
        }

        DateTime Remember(string key, DateTime now)
        {
            DateTime first;
            if (known.TryGet(key, out first)) return first;
            known.Add(key, now);
            return now;
        }

        bool IsNew(DateTime first, DateTime now)
        {
            return first >= learningUntil && now - first < NewWindow;
        }

        static string StoreKey(AppStat app)
        {
            if (app.Path == null) return "name:" + app.Name;
            if (app.Path.EndsWith("\\svchost.exe", StringComparison.OrdinalIgnoreCase))
                return app.Services != null ? "svchost|" + app.Services : null;   // hizmet adı henüz bilinmiyor
            return app.Path.ToLowerInvariant();
        }

        static int LocationScore(AppStat app)
        {
            string p = app.Path.ToLowerInvariant();
            string dir = (Path.GetDirectoryName(p) ?? "").TrimEnd('\\');
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).ToLowerInvariant();
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).ToLowerInvariant();

            if (p.Contains("\\$recycle.bin\\")) return Reason(app, 4, Loc.T("reason.recycle"));
            if (p.Contains("\\appdata\\local\\temp\\") || p.Contains("\\windows\\temp\\"))
                return Reason(app, 3, Loc.T("reason.temp"));
            if (p.Contains("\\users\\public\\")) return Reason(app, 3, Loc.T("reason.public"));
            if (p.Contains("\\perflogs\\")) return Reason(app, 3, Loc.T("reason.perflogs"));
            if (p.Contains("\\downloads\\")) return Reason(app, 2, Loc.T("reason.downloads"));
            if (dir == programData) return Reason(app, 2, Loc.T("reason.programdata"));
            if (dir == roaming) return Reason(app, 2, Loc.T("reason.roaming"));
            return 0;
        }

        static int NameScore(AppStat app, string exe)
        {
            string dir = (Path.GetDirectoryName(app.Path) ?? "").TrimEnd('\\').ToLowerInvariant();
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
            string sys = Environment.SystemDirectory.ToLowerInvariant();
            foreach (string n in SystemNames)
            {
                if (exe != n) continue;
                bool ok = dir == sys || dir == win + "\\syswow64" || dir.StartsWith(win + "\\winsxs")
                    || (n == "explorer" && dir == win);
                return ok ? 0 : Reason(app, 5, Loc.T("reason.masquerade", dir));
            }
            if (exe.Length < 5) return 0;
            foreach (string n in SystemNames)
            {
                if (n.Length >= 5 && Distance(exe, n) == 1)
                    return Reason(app, 4, Loc.T("reason.lookalike", exe, n));
            }
            return 0;
        }

        // Optimal string alignment (yer değiştirme dahil) düzenleme uzaklığı.
        static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                        d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
            return d[a.Length, b.Length];
        }

        static string OddPort(int port)
        {
            switch (port)
            {
                case 4444: case 4445: case 1337: case 31337: case 12345: case 54321: return Loc.T("port.backdoor");
                case 6666: case 6667: case 6668: case 6669: return Loc.T("port.irc");
                case 9001: case 9030: case 9050: case 9150: return Loc.T("port.tor");
                case 3333: case 5555: case 7777: case 14444: case 14433: case 45700: return Loc.T("port.mining");
            }
            return null;
        }

        public static string DomainKey(EndpointStat ep)
        {
            if (!string.IsNullOrEmpty(ep.Host)) return BaseDomain(ep.Host);
            IPAddress ip;
            if (!IPAddress.TryParse(ep.Address, out ip)) return ep.Address;
            byte[] b = ip.GetAddressBytes();
            if (b.Length == 4) return b[0] + "." + b[1] + "." + b[2] + ".0/24";
            return string.Format("{0:x2}{1:x2}:{2:x2}{3:x2}:{4:x2}{5:x2}::/48", b[0], b[1], b[2], b[3], b[4], b[5]);
        }

        static string BaseDomain(string host)
        {
            string[] parts = host.ToLowerInvariant().TrimEnd('.').Split('.');
            if (parts.Length <= 2) return string.Join(".", parts);
            string tld = parts[parts.Length - 1], sld = parts[parts.Length - 2];
            bool country = tld.Length == 2 && (sld == "co" || sld == "com" || sld == "net" || sld == "org" || sld == "gov"
                || sld == "edu" || sld == "ac" || sld == "gen" || sld == "bel" || sld == "web" || sld == "k12");
            int take = country ? 3 : 2;
            return string.Join(".", parts, parts.Length - take, take);
        }

        // ---------- Süreç ayrıntıları (WMI, arka planda) ----------

        public ProcDetails GetProc(int pid)
        {
            lock (gate)
            {
                ProcDetails v;
                if (procCache.TryGetValue(pid, out v)) return v;
                if (!procPending.Add(pid)) return null;
            }
            if (pid == 0 || pid == 4)
            {
                var sys = new ProcDetails { User = "SYSTEM" };
                lock (gate) { procCache[pid] = sys; procPending.Remove(pid); }
                return sys;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                var pd = new ProcDetails();
                try
                {
                    using (var s = new ManagementObjectSearcher(
                        "SELECT CommandLine, ParentProcessId, CreationDate FROM Win32_Process WHERE ProcessId = " + pid))
                    using (var results = s.Get())
                    {
                        foreach (ManagementObject mo in results)
                        {
                            using (mo)
                            {
                                pd.CommandLine = mo["CommandLine"] as string;
                                pd.ParentPid = Convert.ToInt32(mo["ParentProcessId"]);
                                string created = mo["CreationDate"] as string;
                                if (created != null) pd.StartTime = ManagementDateTimeConverter.ToDateTime(created);
                                try
                                {
                                    var owner = mo.InvokeMethod("GetOwner", null, null);
                                    if (owner != null && owner["User"] != null)
                                        pd.User = (owner["Domain"] != null ? owner["Domain"] + "\\" : "") + owner["User"];
                                }
                                catch { }
                            }
                        }
                    }
                    if (pd.ParentPid > 0)
                    {
                        try
                        {
                            var parent = Process.GetProcessById(pd.ParentPid);
                            // PID yeniden kullanılmış olabilir: üst süreç çocuktan sonra başladıysa yok say.
                            if (pd.StartTime == DateTime.MinValue || parent.StartTime <= pd.StartTime) pd.ParentName = parent.ProcessName;
                        }
                        catch { }
                    }
                }
                catch { }
                lock (gate) { procCache[pid] = pd; procPending.Remove(pid); }
            });
            return null;
        }

        // ---------- Dijital imza (Get-AuthenticodeSignature, ayrı iş parçacığında) ----------

        public SignatureInfo GetSignature(string path)
        {
            lock (gate)
            {
                SignatureInfo v;
                if (sigCache.TryGetValue(path, out v)) return v;
                if (!sigPending.Add(path)) return null;
                sigQueue.Enqueue(path);
            }
            sigSignal.Set();
            return null;
        }

        void SignatureWorker()
        {
            Runspace rs = null;
            try
            {
                rs = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault());
                rs.Open();
            }
            catch { rs = null; }

            while (!disposed)
            {
                sigSignal.WaitOne(2000);
                while (!disposed)
                {
                    string path;
                    lock (gate)
                    {
                        if (sigQueue.Count == 0) break;
                        path = sigQueue.Dequeue();
                    }
                    SignatureInfo info = CheckSignature(rs, path);
                    lock (gate) { sigCache[path] = info; sigPending.Remove(path); }
                }
            }
            if (rs != null) { try { rs.Close(); } catch { } }
        }

        static SignatureInfo CheckSignature(Runspace rs, string path)
        {
            var info = new SignatureInfo { Status = "Unknown" };
            if (rs == null) return info;
            try
            {
                using (var ps = PowerShell.Create())
                {
                    ps.Runspace = rs;
                    ps.AddCommand("Get-AuthenticodeSignature").AddParameter("LiteralPath", path);
                    var res = ps.Invoke();
                    if (res.Count == 0) return info;
                    var sig = res[0].BaseObject as Signature;
                    if (sig == null) return info;
                    info.Detail = sig.Status.ToString();
                    switch (sig.Status)
                    {
                        case SignatureStatus.Valid: info.Status = "Valid"; break;
                        case SignatureStatus.NotSigned: info.Status = "NotSigned"; break;
                        case SignatureStatus.HashMismatch:
                        case SignatureStatus.NotTrusted: info.Status = "Invalid"; break;
                    }
                    if (sig.SignerCertificate != null)
                        info.Signer = sig.SignerCertificate.GetNameInfo(X509NameType.SimpleName, false);
                }
            }
            catch { }
            return info;
        }
    }
}
