// TurnScan: finds where P5R stores "which party member is acting" by intersecting memory snapshots.
// Read-only: opens P5R with PROCESS_VM_READ | PROCESS_QUERY_INFORMATION and never writes.
// Usage: run while P5R is open, then at each party member's command menu type that character's unit id.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

static class Program
{
    const uint PROCESS_VM_READ = 0x10, PROCESS_QUERY_INFORMATION = 0x400;
    const uint MEM_COMMIT = 0x1000;
    const uint PAGE_READWRITE = 0x04, PAGE_WRITECOPY = 0x08, PAGE_EXECUTE_READWRITE = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public ulong BaseAddress, AllocationBase;
        public uint AllocationProtect, __alignment1;
        public ulong RegionSize;
        public uint State, Protect, Type, __alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, IntPtr len);

    // Candidate set per value width. Addresses are kept sorted so re-reads can be batched by page.
    static readonly int[] Widths = { 4, 2, 1 };

    static int Main(string[] args)
    {
        var p = Process.GetProcessesByName("P5R").FirstOrDefault();
        if (p == null) { Console.WriteLine("P5R is not running."); return 1; }
        var h = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, p.Id);
        if (h == IntPtr.Zero) { Console.WriteLine($"OpenProcess failed: {Marshal.GetLastWin32Error()}"); return 1; }
        var main = p.MainModule!;
        ulong imgBase = (ulong)main.BaseAddress.ToInt64(), imgEnd = imgBase + (ulong)main.ModuleMemorySize;
        Console.WriteLine($"Attached to P5R pid {p.Id}, image 0x{imgBase:X}-0x{imgEnd:X}");
        Console.WriteLine("Unit ids: 1 Joker, 2 Ryuji, 3 Morgana, 4 Ann, 5 Yusuke, 6 Makoto, 7 Haru, 8 Futaba, 9 Akechi, 10 Kasumi");
        Console.WriteLine("At each party member's command menu, type their id and press Enter. 'u' = undo last, 'q' = quit.\n");

        string log = Path.Combine(AppContext.BaseDirectory, "turnscan.log");

        // Non-interactive mode, so an agent can run one step per chat message:
        //   TurnScan step <id>   (first call scans everything, later calls filter)
        //   TurnScan reset
        string state = Path.Combine(AppContext.BaseDirectory, "turnscan.state");
        if (args.Length > 0)
        {
            if (args[0] == "reset") { File.Delete(state); File.Delete(state + ".steps"); Console.WriteLine("state cleared"); return 0; }
            if (args[0] == "step" && args.Length > 1 && int.TryParse(args[1], out int sid))
            {
                var prev = File.Exists(state) ? Load(state) : null;
                var prevSteps = File.Exists(state + ".steps") ? File.ReadAllText(state + ".steps") : "";
                var sw1 = Stopwatch.StartNew();
                var next = prev == null ? FirstScan(h, sid) : Filter(h, prev, sid);
                Save(state, next);
                var allSteps = (prevSteps.Length > 0 ? prevSteps + "," : "") + sid;
                File.WriteAllText(state + ".steps", allSteps);
                Console.WriteLine($"  scanned in {sw1.ElapsedMilliseconds} ms, ids so far: {allSteps}");
                Report(next, imgBase, imgEnd, log);
                return 0;
            }
            if (args[0] == "watch" && args.Length > 1)
            {
                // TurnScan watch <hexaddr> [seconds]: logs every change of the u16 at addr, plus the bytes around it.
                ulong wa = Convert.ToUInt64(args[1].Replace("0x", ""), 16);
                int secs = args.Length > 2 ? int.Parse(args[2]) : 180;
                var around = new byte[0x40];
                if (ReadProcessMemory(h, (IntPtr)(long)(wa - 0x20), around, 0x40, out _))
                    Console.WriteLine($"bytes 0x{wa - 0x20:X}: {Convert.ToHexString(around)}");
                var cell = new byte[2]; int last = -1; var t = Stopwatch.StartNew();
                string wlog = Path.Combine(AppContext.BaseDirectory, "turnscan-watch.log");
                while (t.Elapsed.TotalSeconds < secs)
                {
                    int v = ReadProcessMemory(h, (IntPtr)(long)wa, cell, 2, out _) ? BitConverter.ToUInt16(cell, 0) : -2;
                    if (v != last)
                    {
                        var msg = $"{DateTime.Now:HH:mm:ss.fff} value={(v == -2 ? "unreadable" : v.ToString())}";
                        Console.WriteLine(msg); File.AppendAllText(wlog, msg + Environment.NewLine); last = v;
                    }
                    Thread.Sleep(20);
                }
                return 0;
            }
            Console.WriteLine("usage: TurnScan [step <id> | reset | watch <hexaddr> [seconds]]"); return 1;
        }

        var cands = new Dictionary<int, List<ulong>>();
        var history = new List<Dictionary<int, List<ulong>>>();
        var steps = new List<int>();

        while (true)
        {
            Console.Write($"[step {steps.Count + 1}] acting unit id> ");
            var line = Console.ReadLine()?.Trim();
            if (line == null || line == "q") break;
            if (line == "u")
            {
                if (history.Count == 0) { Console.WriteLine("nothing to undo"); continue; }
                cands = history[^1]; history.RemoveAt(history.Count - 1); steps.RemoveAt(steps.Count - 1);
                Report(cands, imgBase, imgEnd, null); continue;
            }
            if (!int.TryParse(line, out int id) || id < 0 || id > 255) { Console.WriteLine("enter a number 0-255"); continue; }

            var sw = Stopwatch.StartNew();
            history.Add(cands);
            cands = steps.Count == 0 ? FirstScan(h, id) : Filter(h, cands, id);
            steps.Add(id);
            Console.WriteLine($"  scanned in {sw.ElapsedMilliseconds} ms, ids so far: {string.Join(",", steps)}");
            Report(cands, imgBase, imgEnd, log);
            if (steps.Distinct().Count() < 2) Console.WriteLine("  (need at least 2 different characters before results mean anything)");
        }
        return 0;
    }

    static IEnumerable<(ulong start, ulong size)> Regions(IntPtr h)
    {
        ulong addr = 0;
        while (VirtualQueryEx(h, (IntPtr)(long)addr, out var m, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != IntPtr.Zero)
        {
            uint prot = m.Protect & 0xFF;
            if (m.State == MEM_COMMIT && (prot == PAGE_READWRITE || prot == PAGE_WRITECOPY || prot == PAGE_EXECUTE_READWRITE)
                && (m.Protect & 0x100) == 0 /* skip PAGE_GUARD */)
                yield return (m.BaseAddress, m.RegionSize);
            addr = m.BaseAddress + m.RegionSize;
            if (addr == 0) break;
        }
    }

    static Dictionary<int, List<ulong>> FirstScan(IntPtr h, int id)
    {
        var res = Widths.ToDictionary(w => w, _ => new List<ulong>());
        const int chunk = 1 << 20;
        var buf = new byte[chunk];
        foreach (var (start, size) in Regions(h))
        {
            for (ulong off = 0; off < size; off += chunk)
            {
                int n = (int)Math.Min((ulong)chunk, size - off);
                if (!ReadProcessMemory(h, (IntPtr)(long)(start + off), buf, n, out var got)) continue;
                n = (int)got;
                for (int i = 0; i + 4 <= n; i += 4) if (BitConverter.ToInt32(buf, i) == id) res[4].Add(start + off + (ulong)i);
                for (int i = 0; i + 2 <= n; i += 2) if (BitConverter.ToUInt16(buf, i) == id) res[2].Add(start + off + (ulong)i);
                // Byte candidates explode for small ids; cap at 30M (~240 MB) so the scanner stays bounded.
                if (res[1].Count < 30_000_000) for (int i = 0; i < n; i++) if (buf[i] == id) res[1].Add(start + off + (ulong)i);
            }
        }
        return res;
    }

    static Dictionary<int, List<ulong>> Filter(IntPtr h, Dictionary<int, List<ulong>> cands, int id)
    {
        var res = new Dictionary<int, List<ulong>>();
        var page = new byte[4096];
        foreach (var (w, list) in cands)
        {
            var keep = new List<ulong>();
            ulong curPage = ulong.MaxValue; bool ok = false;
            foreach (var a in list)
            {
                ulong pg = a & ~0xFFFUL;
                if (pg != curPage) { curPage = pg; ok = ReadProcessMemory(h, (IntPtr)(long)pg, page, 4096, out _); }
                if (!ok) continue;
                int o = (int)(a - pg);
                if (o + w > 4096) continue;
                long v = w == 4 ? BitConverter.ToInt32(page, o) : w == 2 ? BitConverter.ToUInt16(page, o) : page[o];
                if (v == id) keep.Add(a);
            }
            res[w] = keep;
        }
        return res;
    }

    static void Save(string path, Dictionary<int, List<ulong>> c)
    {
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write(c.Count);
        foreach (var (w, l) in c) { bw.Write(w); bw.Write(l.Count); foreach (var a in l) bw.Write(a); }
    }

    static Dictionary<int, List<ulong>> Load(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        var c = new Dictionary<int, List<ulong>>();
        int n = br.ReadInt32();
        for (int k = 0; k < n; k++)
        {
            int w = br.ReadInt32(), cnt = br.ReadInt32();
            var l = new List<ulong>(cnt);
            for (int i = 0; i < cnt; i++) l.Add(br.ReadUInt64());
            c[w] = l;
        }
        return c;
    }

    static void Report(Dictionary<int, List<ulong>> cands, ulong imgBase, ulong imgEnd, string? log)
    {
        var sb = new StringBuilder();
        foreach (var w in Widths)
        {
            if (!cands.TryGetValue(w, out var l)) continue;
            sb.AppendLine($"  {w}-byte candidates: {l.Count:N0}");
            if (l.Count <= 40)
                foreach (var a in l)
                    sb.AppendLine(a >= imgBase && a < imgEnd ? $"    0x{a:X}  (P5R.exe+0x{a - imgBase:X})" : $"    0x{a:X}  (heap)");
        }
        Console.Write(sb);
        if (log != null) File.AppendAllText(log, $"{DateTime.Now:O}\n{sb}\n");
    }
}
