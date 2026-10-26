using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpenFusionCAD.Model;

namespace OpenFusionCAD.Kernel;

public sealed class BodyMesh
{
    public int Index;
    public string Name = "";
    public string FeatureId = "";
    public float[] Positions = [];
    public float[] Normals = [];
    public uint[] Indices = [];
    public float[] Edges = [];
    public uint[] EdgeIds = [];
    public uint[] FaceStart = [];
    public float[] FaceInfo = [];
    public double[] BBox = new double[6];
    public int FaceCount => Math.Max(0, FaceStart.Length - 1);
    public int EdgeCount => EdgeIds.Length == 0 ? 0 : (int)EdgeIds.Max() + 1;
}

public sealed class ProfileInfo
{
    public List<double[]> Outer = [];
    public List<List<double[]>> Holes = [];
    public double Area;
    public float[] Tris = [];
}

public sealed class FeatureStatus
{
    public string Id = "";
    public bool Ok = true;
    public string? Error;
}

public sealed class RegenResult
{
    public List<FeatureStatus> Features = [];
    public Dictionary<string, List<ProfileInfo>> Profiles = [];
    public List<BodyMesh> Bodies = [];
    public string? Error;
    public FeatureStatus? StatusOf(string id) => Features.FirstOrDefault(f => f.Id == id);
}

/// <summary>
/// 형상 커널 프로세스(ofc-kernel) 클라이언트.
/// OCCT 가 C++ 예외로 커널을 종료시키면 다시 띄우고, 타임라인을 이분 탐색해
/// 실패한 피처를 찾아 오류로 표시한 뒤 나머지를 계산한다.
/// </summary>
public sealed class KernelClient : IDisposable
{
    Process? _proc;
    readonly SemaphoreSlim _lock = new(1, 1);
    public string KernelPath { get; }

    public KernelClient()
    {
        KernelPath = FindKernel();
    }

    static string FindKernel()
    {
        var env = Environment.GetEnvironmentVariable("OFC_KERNEL");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            foreach (var cand in new[] { Path.Combine(dir.FullName, "ofc-kernel"), Path.Combine(dir.FullName, "target", "release", "ofc-kernel") })
                if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        return "ofc-kernel";
    }

    void EnsureStarted()
    {
        if (_proc is { HasExited: false }) return;
        _proc?.Dispose();
        var psi = new ProcessStartInfo(KernelPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("cannot start ofc-kernel");
        // OCCT 진단 메시지(stderr)는 버린다
        _proc.ErrorDataReceived += (_, _) => { };
        _proc.BeginErrorReadLine();
    }

    sealed class KernelCrashed : Exception { }

    static void ReadExact(Stream s, Span<byte> buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf[got..]);
            if (n <= 0) throw new KernelCrashed();
            got += n;
        }
    }

    static byte[] ReadBlock(Stream s)
    {
        Span<byte> len = stackalloc byte[4];
        ReadExact(s, len);
        var buf = new byte[BitConverter.ToUInt32(len)];
        ReadExact(s, buf);
        return buf;
    }

    static T[] Cast<T>(byte[] b) where T : struct => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(b).ToArray();

    (JsonObject meta, List<byte[][]> blobs) Call(JsonObject req)
    {
        EnsureStarted();
        var line = req.ToJsonString() + "\n";
        try
        {
            _proc!.StandardInput.Write(line);
            _proc.StandardInput.Flush();
            var stream = _proc.StandardOutput.BaseStream;
            var meta = JsonNode.Parse(ReadBlock(stream))!.AsObject();
            var blobs = new List<byte[][]>();
            if (meta["meshed"] is JsonArray meshed)
                foreach (var _ in meshed)
                    blobs.Add(Enumerable.Range(0, 7).Select(_ => ReadBlock(stream)).ToArray());
            return (meta, blobs);
        }
        catch (IOException) { throw new KernelCrashed(); }
    }

    public async Task<RegenResult> RegenerateAsync(Document doc) => await Task.Run(() => Regenerate(doc));

    public RegenResult Regenerate(Document doc)
    {
        _lock.Wait();
        try
        {
            var work = doc.Clone();
            var crashed = new List<string>();
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    var r = Parse(Call(new JsonObject { ["cmd"] = "regen", ["doc"] = JsonNode.Parse(work.ToJson()) }));
                    foreach (var id in crashed)
                    {
                        var st = r.StatusOf(id);
                        if (st != null) { st.Ok = false; st.Error = "형상 연산에 실패했습니다 (값을 줄이거나 다른 모서리를 선택해 보세요)"; }
                        else r.Features.Add(new FeatureStatus { Id = id, Ok = false, Error = "형상 연산에 실패했습니다" });
                    }
                    return r;
                }
                catch (KernelCrashed)
                {
                    // 커널이 죽었다: 마커를 이분 탐색해 처음으로 죽는 피처를 찾아 억제하고 다시 계산
                    var bad = FindCrashingFeature(work);
                    if (bad == null) return new RegenResult { Error = "형상 커널이 예기치 않게 종료되었습니다." };
                    bad.Suppressed = true;
                    crashed.Add(bad.Id);
                }
            }
            return new RegenResult { Error = "형상 커널이 반복해서 종료되었습니다." };
        }
        finally { _lock.Release(); }
    }

    Feature? FindCrashingFeature(Document doc)
    {
        int n = doc.ActiveCount, lo = 1, hi = n;
        bool Crashes(int marker)
        {
            var d = doc.Clone();
            d.Marker = marker;
            try { Call(new JsonObject { ["cmd"] = "regen", ["doc"] = JsonNode.Parse(d.ToJson()) }); return false; }
            catch (KernelCrashed) { return true; }
        }
        if (!Crashes(n)) return null;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Crashes(mid)) hi = mid; else lo = mid + 1;
        }
        return doc.Features[lo - 1];
    }

    static RegenResult Parse((JsonObject meta, List<byte[][]> blobs) resp)
    {
        var (meta, blobs) = resp;
        var r = new RegenResult();
        if (meta["ok"]?.GetValue<bool>() != true) { r.Error = meta["error"]?.ToString(); return r; }
        foreach (var f in meta["features"]!.AsArray())
            r.Features.Add(new FeatureStatus { Id = f!["id"]!.GetValue<string>(), Ok = f["ok"]!.GetValue<bool>(), Error = f["error"]?.GetValue<string>() });
        foreach (var s in meta["sketches"]!.AsArray())
        {
            var list = new List<ProfileInfo>();
            foreach (var p in s!["profiles"]!.AsArray())
            {
                list.Add(new ProfileInfo
                {
                    Outer = p!["outer"]!.AsArray().Select(v => new[] { v![0]!.GetValue<double>(), v[1]!.GetValue<double>() }).ToList(),
                    Holes = p["holes"]!.AsArray().Select(h => h!.AsArray().Select(v => new[] { v![0]!.GetValue<double>(), v[1]!.GetValue<double>() }).ToList()).ToList(),
                    Area = p["area"]!.GetValue<double>(),
                    Tris = p["tris"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray(),
                });
            }
            r.Profiles[s["id"]!.GetValue<string>()] = list;
        }
        var meshed = meta["meshed"]!.AsArray().Select(x => x!.GetValue<int>()).ToList();
        var bodies = meta["bodies"]!.AsArray();
        for (int k = 0; k < meshed.Count; k++)
        {
            var info = bodies.First(b => b!["index"]!.GetValue<int>() == meshed[k])!;
            var b = blobs[k];
            r.Bodies.Add(new BodyMesh
            {
                Index = meshed[k],
                Name = info["name"]!.GetValue<string>(),
                FeatureId = info["feature"]!.GetValue<string>(),
                BBox = info["bbox"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray(),
                Positions = Cast<float>(b[0]),
                Normals = Cast<float>(b[1]),
                Indices = Cast<uint>(b[2]),
                Edges = Cast<float>(b[3]),
                EdgeIds = Cast<uint>(b[4]),
                FaceStart = Cast<uint>(b[5]),
                FaceInfo = Cast<float>(b[6]),
            });
        }
        return r;
    }

    /// STEP/STL/IGES/BREP 내보내기. 반환: 오류 메시지 또는 null
    public async Task<string?> ExportAsync(Document doc, string path, string? format = null, IEnumerable<int>? bodies = null)
    {
        await _lock.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                var req = new JsonObject { ["cmd"] = "export", ["doc"] = JsonNode.Parse(doc.ToJson()), ["path"] = path };
                if (format != null) req["format"] = format;
                if (bodies != null) req["bodies"] = new JsonArray(bodies.Select(b => (JsonNode)b).ToArray());
                try
                {
                    var (meta, _) = Call(req);
                    return meta["ok"]?.GetValue<bool>() == true ? null : meta["error"]?.ToString() ?? "export failed";
                }
                catch (KernelCrashed) { return "형상 커널이 내보내기 중 종료되었습니다."; }
            });
        }
        finally { _lock.Release(); }
    }

    public void Dispose()
    {
        try { _proc?.Kill(); } catch { }
        _proc?.Dispose();
    }
}
