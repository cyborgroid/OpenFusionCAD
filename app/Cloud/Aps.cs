using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenFusionCAD.Cloud;

/// <summary>
/// Autodesk Platform Services (APS) 계정 연동.
/// 3-legged OAuth 2.0 + PKCE (공개 클라이언트, Client Secret 없음). 로그인은 시스템 기본 브라우저에서
/// Autodesk 공식 로그인 페이지로 진행되고, 로컬 루프백 주소로 인가 코드를 받는다.
/// 토큰은 ~/.config/openfusioncad/aps_token.json (권한 600) 에 저장한다.
/// </summary>
public sealed class ApsSession
{
    const string AuthBase = "https://developer.api.autodesk.com/authentication/v2";
    const string Api = "https://developer.api.autodesk.com";
    const string Scope = "data:read user-profile:read";

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    readonly string _tokenFile;
    TokenSet? _token;

    public string ClientId { get; set; }
    public string RedirectUri { get; set; }
    public int Port { get; set; }
    public string? UserName { get; private set; }
    public string? Email { get; private set; }
    public bool SignedIn => _token != null;

    sealed class TokenSet
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTimeOffset Expires { get; set; }
    }

    public ApsSession(string configDir, string clientId, int port)
    {
        ClientId = clientId;
        Port = port;
        RedirectUri = $"http://localhost:{port}/oauth/callback";
        _tokenFile = Path.Combine(configDir, "aps_token.json");
        try
        {
            if (File.Exists(_tokenFile)) _token = JsonSerializer.Deserialize<TokenSet>(File.ReadAllText(_tokenFile));
        }
        catch { _token = null; }
    }

    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// 브라우저 로그인 → 루프백으로 인가 코드 수신 → 토큰 교환
    public async Task SignInAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ClientId)) throw new InvalidOperationException("APS Client ID 가 설정되지 않았습니다.");
        var verifier = B64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = B64Url(RandomNumberGenerator.GetBytes(16));
        var url = $"{AuthBase}/authorize?response_type=code&client_id={Uri.EscapeDataString(ClientId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={Uri.EscapeDataString(Scope)}" +
                  $"&code_challenge={challenge}&code_challenge_method=S256&state={state}";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{Port}/oauth/");
        listener.Start();
        OpenBrowser(url);

        string? code = null, error = null;
        using (ct.Register(() => { try { listener.Stop(); } catch { } }))
        {
            while (code == null && error == null)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
                var q = ctx.Request.QueryString;
                if (ctx.Request.Url?.AbsolutePath != "/oauth/callback") { ctx.Response.StatusCode = 404; ctx.Response.Close(); continue; }
                if (q["state"] != state) error = "state 불일치 (다른 로그인 요청의 응답)";
                else if (q["error"] != null) error = $"{q["error"]}: {q["error_description"]}";
                else code = q["code"];
                var html = code != null
                    ? "<html><body style='font-family:sans-serif;text-align:center;margin-top:80px'><h2>OpenFusionCAD 로그인 완료</h2><p>이 창을 닫고 앱으로 돌아가십시오.</p></body></html>"
                    : $"<html><body style='font-family:sans-serif;text-align:center;margin-top:80px'><h2>로그인 실패</h2><p>{WebUtility.HtmlEncode(error)}</p></body></html>";
                var bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                ctx.Response.Close();
            }
        }
        if (error != null) throw new InvalidOperationException(error);

        await TokenRequest(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
        }, ct);
        await LoadProfile(ct);
    }

    async Task TokenRequest(Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await _http.PostAsync($"{AuthBase}/token", new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"토큰 요청 실패 ({(int)resp.StatusCode}): {body}");
        var j = JsonNode.Parse(body)!;
        _token = new TokenSet
        {
            AccessToken = j["access_token"]!.GetValue<string>(),
            RefreshToken = j["refresh_token"]?.GetValue<string>() ?? _token?.RefreshToken ?? "",
            Expires = DateTimeOffset.UtcNow.AddSeconds(j["expires_in"]?.GetValue<int>() ?? 3600),
        };
        SaveToken();
    }

    void SaveToken()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_tokenFile)!);
        File.WriteAllText(_tokenFile, JsonSerializer.Serialize(_token));
        try { File.SetUnixFileMode(_tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    }

    public void SignOut()
    {
        _token = null; UserName = null; Email = null;
        try { File.Delete(_tokenFile); } catch { }
    }

    async Task<string> AccessToken(CancellationToken ct)
    {
        if (_token == null) throw new InvalidOperationException("로그인이 필요합니다.");
        if (_token.Expires < DateTimeOffset.UtcNow.AddMinutes(2))
        {
            if (string.IsNullOrEmpty(_token.RefreshToken)) { SignOut(); throw new InvalidOperationException("다시 로그인하십시오."); }
            try
            {
                await TokenRequest(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = _token.RefreshToken,
                    ["client_id"] = ClientId,
                    ["scope"] = Scope,
                }, ct);
            }
            catch { SignOut(); throw new InvalidOperationException("세션이 만료되었습니다. 다시 로그인하십시오."); }
        }
        return _token!.AccessToken;
    }

    async Task<JsonNode> Get(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url.StartsWith("http") ? url : Api + url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken(ct));
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"APS 요청 실패 ({(int)resp.StatusCode}) {url}");
        return JsonNode.Parse(body)!;
    }

    public async Task LoadProfile(CancellationToken ct = default)
    {
        var j = await Get("https://api.userprofile.autodesk.com/userinfo", ct);
        UserName = j["name"]?.GetValue<string>() ?? j["preferred_username"]?.GetValue<string>();
        Email = j["email"]?.GetValue<string>();
    }

    public sealed record Node(string Kind, string Id, string Name, string? ProjectId = null, string? HubId = null, string? FileType = null, string? StorageId = null);

    public async Task<List<Node>> Hubs(CancellationToken ct = default) =>
        (await Get("/project/v1/hubs", ct))["data"]!.AsArray()
            .Select(h => new Node("hub", h!["id"]!.GetValue<string>(), h["attributes"]!["name"]!.GetValue<string>())).ToList();

    public async Task<List<Node>> Projects(string hubId, CancellationToken ct = default) =>
        (await Get($"/project/v1/hubs/{Uri.EscapeDataString(hubId)}/projects", ct))["data"]!.AsArray()
            .Select(p => new Node("project", p!["id"]!.GetValue<string>(), p["attributes"]!["name"]!.GetValue<string>(), p["id"]!.GetValue<string>(), hubId)).ToList();

    public async Task<List<Node>> TopFolders(string hubId, string projectId, CancellationToken ct = default) =>
        (await Get($"/project/v1/hubs/{Uri.EscapeDataString(hubId)}/projects/{Uri.EscapeDataString(projectId)}/topFolders", ct))["data"]!.AsArray()
            .Select(f => new Node("folder", f!["id"]!.GetValue<string>(), f["attributes"]!["displayName"]?.GetValue<string>() ?? f["attributes"]!["name"]!.GetValue<string>(), projectId, hubId)).ToList();

    public async Task<List<Node>> FolderContents(string projectId, string folderId, CancellationToken ct = default)
    {
        var j = await Get($"/data/v1/projects/{Uri.EscapeDataString(projectId)}/folders/{Uri.EscapeDataString(folderId)}/contents", ct);
        var versions = (j["included"]?.AsArray() ?? []).Where(v => v?["type"]?.GetValue<string>() == "versions").ToList();
        var list = new List<Node>();
        foreach (var d in j["data"]!.AsArray())
        {
            var type = d!["type"]!.GetValue<string>();
            var id = d["id"]!.GetValue<string>();
            var name = d["attributes"]!["displayName"]?.GetValue<string>() ?? d["attributes"]!["name"]?.GetValue<string>() ?? id;
            if (type == "folders") { list.Add(new Node("folder", id, name, projectId)); continue; }
            var tipId = d["relationships"]?["tip"]?["data"]?["id"]?.GetValue<string>();
            var ver = versions.FirstOrDefault(v => v!["id"]!.GetValue<string>() == tipId);
            var fileType = ver?["attributes"]?["fileType"]?.GetValue<string>() ?? Path.GetExtension(name).TrimStart('.');
            var storage = ver?["relationships"]?["storage"]?["data"]?["id"]?.GetValue<string>();
            list.Add(new Node("item", id, name, projectId, null, fileType?.ToLowerInvariant(), storage));
        }
        return list.OrderBy(n => n.Kind != "folder").ThenBy(n => n.Name).ToList();
    }

    /// OSS 객체(urn:adsk.objects:os.object:bucket/key) 다운로드
    public async Task Download(string storageId, string dest, CancellationToken ct = default)
    {
        var rest = storageId[(storageId.LastIndexOf(':') + 1)..];
        var slash = rest.IndexOf('/');
        var bucket = rest[..slash]; var key = rest[(slash + 1)..];
        var j = await Get($"/oss/v2/buckets/{Uri.EscapeDataString(bucket)}/objects/{Uri.EscapeDataString(key)}/signeds3download", ct);
        var url = j["url"]!.GetValue<string>();
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await using var fs = File.Create(dest);
        await resp.Content.CopyToAsync(fs, ct);
    }

    static void OpenBrowser(string url)
    {
        try
        {
            var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            psi.ArgumentList.Add(url);
            Process.Start(psi);
        }
        catch { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    }
}
