using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DwDocExport;

/// <summary>
/// Offener Datei-Download im Originalformat: stellt den Antwort-Stream und den
/// aus Content-Disposition ermittelten Dateinamen bereit. Muss mit using/Dispose
/// freigegeben werden, da die HTTP-Antwort gehalten wird (Streaming).
/// </summary>
public sealed class DownloadStream : IDisposable
{
    private readonly HttpResponseMessage _resp;
    public string FileName { get; }
    public Stream Content { get; }

    internal DownloadStream(HttpResponseMessage resp, string fileName, Stream content)
    {
        _resp = resp;
        FileName = fileName;
        Content = content;
    }

    public void Dispose() => _resp.Dispose();
}

/// <summary>Eine Seite Dokumente plus optionalem Folge-Link (HATEOAS "next").</summary>
public sealed record DocumentPage(List<DwDocument> Items, string? NextUrl);

/// <summary>Kurzinfo zu einem Archiv (DocuWare File Cabinet) – ohne Baskets.</summary>
public sealed record FileCabinetInfo(string Id, string Name);

/// <summary>
/// Kapselt sämtliche Kommunikation mit der DocuWare Platform REST API.
/// Unterstützt Cookie-Login (klassisch / On-Prem) sowie Token-Login über den
/// DocuWare Identity Service (Resource Owner Password ODER – bei gesetztem
/// Client-Secret – Client-Credentials einer App-Registrierung).
/// Downloads erfolgen dateityp-neutral im Originalformat und werden gestreamt.
/// </summary>
public sealed class DocuWareClient : IDisposable
{
    private readonly ExporterOptions _opt;
    private readonly Action<string>? _log;

    private readonly CookieContainer _cookies = new();
    private readonly HttpClientHandler _handler;
    private readonly HttpClient _http;
    private readonly string _serverRoot;

    // Aktiver Authentifizierungszustand
    private AuthMode _effectiveMode = AuthMode.Cookie;
    private string? _accessToken;
    private string? _refreshToken;
    private string? _tokenEndpoint;
    private DateTimeOffset _tokenExpiresUtc = DateTimeOffset.MinValue;
    // Vom Identity Service laut OpenID-Discovery tatsächlich unterstützte Scopes.
    private string[] _supportedScopes = Array.Empty<string>();
    // Letzter Fehlertext einer Token-Anforderung (für aussagekräftige Meldungen).
    private string? _lastTokenError;

    // Standardparameter für den DocuWare Identity Service (Resource Owner Password Grant).
    // Verifiziert gegen DocuWare-Doku (KBA-37505 / developer.docuware.com OAuth Support).
    private const string DefaultClientId = "docuware.platform.net.client";
    // Wunsch-Scopes; werden vor der Anforderung gegen scopes_supported gefiltert,
    // damit ein nicht angebotener Scope (z. B. offline_access) keinen invalid_scope auslöst.
    private static readonly string[] PasswordScopes =
        { "docuware.platform", "dwprofile", "openid", "offline_access" };
    private static readonly string[] ClientCredentialsScopes = { "docuware.platform" };
    // Pflicht-Scope, der immer angefordert wird (auch wenn die Discovery nichts liefert).
    private const string EssentialScope = "docuware.platform";

    /// <summary>Basis-URL der Plattform, z. B. https://server/DocuWare/Platform</summary>
    public string PlatformBaseUrl { get; }

    public DocuWareClient(ExporterOptions opt, Action<string>? log = null)
    {
        _opt = opt;
        _log = log;

        _handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        // Proxy (optional)
        if (!string.IsNullOrWhiteSpace(_opt.ProxyUrl))
        {
            _handler.Proxy = new WebProxy(_opt.ProxyUrl);
            _handler.UseProxy = true;
        }

        // TLS: Zertifikatsfehler ignorieren bzw. eigenes CA-Zertifikat vertrauen.
        if (_opt.IgnoreCertErrors || !string.IsNullOrWhiteSpace(_opt.CustomCaPath))
            _handler.ServerCertificateCustomValidationCallback = ValidateServerCertificate;

        _http = new HttpClient(_handler)
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DwDocExport/1.0");

        var server = (_opt.Server ?? string.Empty).TrimEnd('/');
        PlatformBaseUrl = $"{server}/DocuWare/Platform";

        // Wurzel (Schema + Host) zum Auflösen relativer HATEOAS-Links.
        try
        {
            var u = new Uri(server);
            _serverRoot = $"{u.Scheme}://{u.Authority}";
        }
        catch
        {
            _serverRoot = server;
        }
    }

    private void Log(string msg) => _log?.Invoke(msg);

    /// <summary>
    /// Validierungs-Callback für Server-Zertifikate: akzeptiert bei
    /// IgnoreCertErrors alles; bei gesetztem CustomCaPath, wenn das vorgelegte
    /// Zertifikat (oder seine Kette) mit dem eigenen CA-Zertifikat übereinstimmt.
    /// </summary>
    private bool ValidateServerCertificate(HttpRequestMessage req, X509Certificate2? cert,
        X509Chain? chain, System.Net.Security.SslPolicyErrors errors)
    {
        if (_opt.IgnoreCertErrors)
            return true;
        if (errors == System.Net.Security.SslPolicyErrors.None)
            return true;

        if (!string.IsNullOrWhiteSpace(_opt.CustomCaPath) && cert != null)
        {
            try
            {
                using var ca = new X509Certificate2(_opt.CustomCaPath);
                if (string.Equals(cert.Thumbprint, ca.Thumbprint, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (chain != null)
                    foreach (var el in chain.ChainElements)
                        if (string.Equals(el.Certificate.Thumbprint, ca.Thumbprint, StringComparison.OrdinalIgnoreCase))
                            return true;
            }
            catch { /* ungültiges CA -> ablehnen */ }
        }
        return false;
    }

    /// <summary>Baut den Download-Query-String aus den Optionen (Zielformat, Annotationen).</summary>
    private string DownloadQuery()
    {
        var fileType = string.IsNullOrWhiteSpace(_opt.TargetFileType) ? "Auto" : _opt.TargetFileType;
        var keep = _opt.KeepAnnotations ? "true" : "false";
        return $"?targetFileType={Uri.EscapeDataString(fileType)}&keepAnnotations={keep}";
    }

    // =====================================================================
    //  Authentifizierung
    // =====================================================================

    public async Task AuthenticateAsync(CancellationToken ct)
    {
        switch (_opt.AuthMode)
        {
            case AuthMode.Token:
                await AuthenticateTokenAsync(ct).ConfigureAwait(false);
                _effectiveMode = AuthMode.Token;
                break;

            case AuthMode.Cookie:
                await AuthenticateCookieAsync(ct).ConfigureAwait(false);
                _effectiveMode = AuthMode.Cookie;
                break;

            default: // Auto
                try
                {
                    await AuthenticateTokenAsync(ct).ConfigureAwait(false);
                    _effectiveMode = AuthMode.Token;
                    Log("Anmeldung über Identity Service (Token) erfolgreich.");
                }
                catch (Exception ex)
                {
                    Log($"Token-Anmeldung nicht möglich ({ex.Message}); Wechsel auf Cookie-Login.");
                    await AuthenticateCookieAsync(ct).ConfigureAwait(false);
                    _effectiveMode = AuthMode.Cookie;
                    Log("Anmeldung über Cookie erfolgreich.");
                }
                break;
        }
    }

    /// <summary>Klassischer Cookie-Login (POST Account/Logon); Cookies bleiben im Handler.</summary>
    private async Task AuthenticateCookieAsync(CancellationToken ct)
    {
        ClearCookies();
        _accessToken = null;
        _http.DefaultRequestHeaders.Authorization = null;

        var form = new Dictionary<string, string>
        {
            ["UserName"] = _opt.User,
            ["Password"] = _opt.Password,
            ["Organization"] = _opt.Organization,
            ["RememberMe"] = "false"
        };

        using var content = new FormUrlEncodedContent(form);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{PlatformBaseUrl}/Account/Logon")
        {
            Content = content
        };
        req.Headers.Accept.ParseAdd("application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(resp).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Cookie-Login fehlgeschlagen (HTTP {(int)resp.StatusCode}). {body}");
        }
    }

    /// <summary>Token-Login über den Identity Service (Discovery + Grant).</summary>
    private async Task AuthenticateTokenAsync(CancellationToken ct)
    {
        ClearCookies();

        var identityServiceUrl = await GetIdentityServiceUrlAsync(ct).ConfigureAwait(false);
        _tokenEndpoint = await GetTokenEndpointAsync(identityServiceUrl, ct).ConfigureAwait(false);

        await RequestTokenAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ermittelt die URL des zuständigen Identity Service. DocuWare stellt diese
    /// je nach Version unter {Platform}/Home/IdentityServiceInfo (gängig) oder
    /// {Platform}/Account/IdentityServiceInfo bereit. Beide Pfade werden probiert.
    /// </summary>
    private async Task<string> GetIdentityServiceUrlAsync(CancellationToken ct)
    {
        string[] candidates =
        {
            $"{PlatformBaseUrl}/Home/IdentityServiceInfo",
            $"{PlatformBaseUrl}/Account/IdentityServiceInfo"
        };

        Exception? last = null;
        foreach (var endpoint in candidates)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                req.Headers.Accept.ParseAdd("application/json");

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    last = new InvalidOperationException(
                        $"IdentityServiceInfo nicht verfügbar (HTTP {(int)resp.StatusCode}) unter {endpoint}.");
                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                var url = TryGetString(doc.RootElement, "IdentityServiceUrl")
                          ?? TryGetString(doc.RootElement, "identityServiceUrl");

                if (!string.IsNullOrWhiteSpace(url))
                    return url.TrimEnd('/');

                last = new InvalidOperationException("Keine Identity-Service-URL in der Antwort gefunden.");
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("Identity Service konnte nicht ermittelt werden.");
    }

    /// <summary>Liest die OpenID-Discovery und liefert den token_endpoint.</summary>
    private async Task<string> GetTokenEndpointAsync(string identityServiceUrl, CancellationToken ct)
    {
        var discoveryUrl = $"{identityServiceUrl}/.well-known/openid-configuration";
        using var req = new HttpRequestMessage(HttpMethod.Get, discoveryUrl);
        req.Headers.Accept.ParseAdd("application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var endpoint = TryGetString(doc.RootElement, "token_endpoint");
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("Kein token_endpoint in der OpenID-Konfiguration gefunden.");

        // Unterstützte Scopes merken, um die Anforderung darauf einzuschränken.
        if (doc.RootElement.TryGetProperty("scopes_supported", out var scopesEl)
            && scopesEl.ValueKind == JsonValueKind.Array)
        {
            _supportedScopes = scopesEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
        }

        return endpoint;
    }

    /// <summary>
    /// Fordert ein Access-Token an: Bei gesetztem Client-Secret per
    /// Client-Credentials (App-Registrierung), sonst per Resource Owner Password.
    /// </summary>
    private async Task RequestTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_tokenEndpoint))
            throw new InvalidOperationException("token_endpoint ist nicht gesetzt.");

        Dictionary<string, string> form;

        if (!string.IsNullOrWhiteSpace(_opt.OAuthClientSecret))
        {
            // App-Registrierung: Client-Credentials-Grant.
            form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = BuildScope(ClientCredentialsScopes),
                ["client_id"] = string.IsNullOrWhiteSpace(_opt.OAuthClientId) ? DefaultClientId : _opt.OAuthClientId,
                ["client_secret"] = _opt.OAuthClientSecret
            };
            if (!string.IsNullOrWhiteSpace(_opt.Organization))
                form["acr_values"] = $"organization:{_opt.Organization}";
        }
        else
        {
            // Klassisch: Resource Owner Password Grant.
            form = new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["scope"] = BuildScope(PasswordScopes),
                ["client_id"] = string.IsNullOrWhiteSpace(_opt.OAuthClientId) ? DefaultClientId : _opt.OAuthClientId,
                ["username"] = _opt.User,
                ["password"] = _opt.Password
            };
            // Organisation bei mehreren Organisationen via acr_values (instanzabhängig).
            if (!string.IsNullOrWhiteSpace(_opt.Organization))
                form["acr_values"] = $"organization:{_opt.Organization}";
        }

        var body = await PostTokenFormAsync(form, ct).ConfigureAwait(false);

        // Sicherheitsnetz: Lehnt der Server einen Scope ab (invalid_scope), Wiederholung
        // nur mit dem Pflicht-Scope docuware.platform – das funktioniert auf allen Tenants.
        if (body is null && form.TryGetValue("scope", out var usedScope)
            && !string.Equals(usedScope, EssentialScope, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Invoke($"Scope \"{usedScope}\" abgelehnt – erneuter Versuch nur mit \"{EssentialScope}\".");
            form["scope"] = EssentialScope;
            body = await PostTokenFormAsync(form, ct).ConfigureAwait(false);
        }

        if (body is null)
            throw new InvalidOperationException(_lastTokenError ?? "Token-Anforderung fehlgeschlagen.");

        ApplyTokenResponse(body);
    }

    /// <summary>
    /// Sendet das Token-Formular. Liefert den Antwort-Body bei Erfolg, andernfalls
    /// <c>null</c>, wenn der Server den Scope ablehnt (invalid_scope) und ein erneuter
    /// Versuch sinnvoll ist. Bei allen anderen Fehlern wird eine Ausnahme geworfen.
    /// </summary>
    private async Task<string?> PostTokenFormAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var req = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint) { Content = content };
        req.Headers.Accept.ParseAdd("application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (resp.IsSuccessStatusCode)
            return body;

        _lastTokenError = $"Token-Anforderung fehlgeschlagen (HTTP {(int)resp.StatusCode}). {body}";

        // invalid_scope signalisiert: mit reduziertem Scope erneut versuchen.
        if (resp.StatusCode == HttpStatusCode.BadRequest
            && body.IndexOf("invalid_scope", StringComparison.OrdinalIgnoreCase) >= 0)
            return null;

        throw new InvalidOperationException(_lastTokenError);
    }

    /// <summary>
    /// Stellt den Scope-Parameter zusammen. Sind die vom Identity Service angebotenen
    /// Scopes bekannt (scopes_supported aus der Discovery), werden nur unterstützte
    /// Wunsch-Scopes angefordert; so kann ein nicht angebotener Scope keinen
    /// invalid_scope-Fehler verursachen. Andernfalls Rückfall auf den Pflicht-Scope.
    /// </summary>
    private string BuildScope(IEnumerable<string> desired)
    {
        if (_supportedScopes.Length == 0)
            return EssentialScope;

        var supported = new HashSet<string>(_supportedScopes, StringComparer.OrdinalIgnoreCase);
        var selected = desired.Where(supported.Contains).ToList();

        // docuware.platform ist zwingend nötig, auch falls es nicht in der Liste auftaucht.
        if (!selected.Any(s => string.Equals(s, EssentialScope, StringComparison.OrdinalIgnoreCase)))
            selected.Insert(0, EssentialScope);

        return string.Join(' ', selected);
    }

    /// <summary>Erneuert das Access-Token über das refresh_token (sonst Neuanforderung).</summary>
    private async Task RefreshTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_tokenEndpoint) || string.IsNullOrEmpty(_refreshToken))
        {
            await RequestTokenAsync(ct).ConfigureAwait(false);
            return;
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = string.IsNullOrWhiteSpace(_opt.OAuthClientId) ? DefaultClientId : _opt.OAuthClientId,
            ["refresh_token"] = _refreshToken
        };

        using var content = new FormUrlEncodedContent(form);
        using var req = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint) { Content = content };
        req.Headers.Accept.ParseAdd("application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            await RequestTokenAsync(ct).ConfigureAwait(false);
            return;
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ApplyTokenResponse(body);
    }

    private void ApplyTokenResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        _accessToken = TryGetString(root, "access_token")
            ?? throw new InvalidOperationException("Antwort enthält kein access_token.");
        _refreshToken = TryGetString(root, "refresh_token") ?? _refreshToken;

        var expiresIn = 3600;
        if (root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var v))
            expiresIn = v;

        _tokenExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    private async Task EnsureTokenFreshAsync(CancellationToken ct)
    {
        if (_effectiveMode != AuthMode.Token)
            return;
        if (DateTimeOffset.UtcNow < _tokenExpiresUtc && _accessToken != null)
            return;

        await RefreshTokenAsync(ct).ConfigureAwait(false);
    }

    private async Task ReAuthenticateAsync(CancellationToken ct)
    {
        if (_effectiveMode == AuthMode.Token)
            await RefreshTokenAsync(ct).ConfigureAwait(false);
        else
            await AuthenticateCookieAsync(ct).ConfigureAwait(false);
    }

    // =====================================================================
    //  Anfragen mit Retry / Auto-Reauth
    // =====================================================================

    /// <summary>
    /// Sendet eine Anfrage mit Wiederholungslogik:
    /// 401 -&gt; neu authentifizieren und einmal erneut; 429/5xx -&gt; Backoff
    /// (Retry-After-Header wird respektiert, sonst exponentiell), bis MaxRetries.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completion,
        CancellationToken ct)
    {
        var maxRetries = Math.Max(0, _opt.MaxRetries);
        var reauthDone = false;

        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await EnsureTokenFreshAsync(ct).ConfigureAwait(false);

            HttpResponseMessage resp;
            using (var req = requestFactory())
            {
                resp = await _http.SendAsync(req, completion, ct).ConfigureAwait(false);
            }

            if (resp.StatusCode == HttpStatusCode.Unauthorized && !reauthDone)
            {
                resp.Dispose();
                reauthDone = true;
                Log("HTTP 401 – erneute Authentifizierung.");
                await ReAuthenticateAsync(ct).ConfigureAwait(false);
                continue;
            }

            var transient = resp.StatusCode == (HttpStatusCode)429 || (int)resp.StatusCode >= 500;
            if (transient && attempt < maxRetries)
            {
                var delay = GetRetryAfter(resp) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                resp.Dispose();
                Log($"Vorübergehender Fehler – erneuter Versuch in {delay.TotalSeconds:0}s.");
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            return resp;
        }
    }

    /// <summary>Liest den Retry-After-Header (Sekunden oder HTTP-Datum), falls vorhanden.</summary>
    private static TimeSpan? GetRetryAfter(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra == null)
            return null;
        if (ra.Delta.HasValue)
            return ra.Delta.Value;
        if (ra.Date.HasValue)
        {
            var diff = ra.Date.Value - DateTimeOffset.UtcNow;
            return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
        }
        return null;
    }

    // =====================================================================
    //  Plattform-Operationen
    // =====================================================================

    /// <summary>Liefert alle Archive (echte File Cabinets, keine Baskets).</summary>
    public async Task<List<FileCabinetInfo>> GetFileCabinetsAsync(CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(
            () => JsonGet($"{PlatformBaseUrl}/FileCabinets"),
            HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var list = new List<FileCabinetInfo>();
        if (doc.RootElement.TryGetProperty("FileCabinet", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var fc in arr.EnumerateArray())
            {
                if (fc.TryGetProperty("IsBasket", out var isBasket) &&
                    isBasket.ValueKind == JsonValueKind.True)
                    continue;

                var id = TryGetString(fc, "Id");
                var name = TryGetString(fc, "Name") ?? id ?? "(ohne Namen)";
                if (!string.IsNullOrEmpty(id))
                    list.Add(new FileCabinetInfo(id, name));
            }
        }
        return list;
    }

    /// <summary>Liefert die Indexfeld-Namen (DBFieldName) eines Archivs.</summary>
    public async Task<List<string>> GetFieldNamesAsync(string fileCabinetId, CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(
            () => JsonGet($"{PlatformBaseUrl}/FileCabinets/{fileCabinetId}"),
            HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var names = new List<string>();
        if (doc.RootElement.TryGetProperty("Fields", out var fields) &&
            fields.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fields.EnumerateArray())
            {
                // Technischer Feldname; identisch zum "FieldName" in den Dokumentdaten.
                // (DocuWare-Schema: DBFieldName, früher fälschlich als DBName gelesen.)
                var dbName = TryGetString(f, "DBFieldName")
                             ?? TryGetString(f, "DBName")
                             ?? TryGetString(f, "DbName");
                var display = TryGetString(f, "DisplayName") ?? TryGetString(f, "Name");
                if (string.IsNullOrEmpty(dbName))
                    continue;

                // Beim Laden im Log auch den Anzeigenamen zeigen, damit das richtige
                // Feld leichter erkannt wird (das Dropdown nutzt den technischen Namen).
                if (!string.IsNullOrEmpty(display) &&
                    !string.Equals(display, dbName, StringComparison.OrdinalIgnoreCase))
                    _log?.Invoke($"Feld: {dbName}  (Anzeige: {display})");

                names.Add(dbName);
            }
        }
        return names;
    }

    /// <summary>Liefert die Gesamtzahl der Dokumente im Archiv.</summary>
    public async Task<int> GetDocumentCountAsync(string fileCabinetId, CancellationToken ct)
    {
        var url = $"{PlatformBaseUrl}/FileCabinets/{fileCabinetId}/Documents?start=0&count=1&calculateTotalCount=true";
        using var resp = await SendWithRetryAsync(
            () => JsonGet(url), HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("Count", out var countObj))
        {
            if (countObj.ValueKind == JsonValueKind.Number && countObj.TryGetInt32(out var direct))
                return direct;
            if (countObj.ValueKind == JsonValueKind.Object &&
                countObj.TryGetProperty("Total", out var total) && total.TryGetInt32(out var t))
                return t;
        }
        return 0;
    }

    /// <summary>Baut die URL der ersten Dokumentseite.</summary>
    public string BuildFirstPageUrl(string fileCabinetId, int count) =>
        $"{PlatformBaseUrl}/FileCabinets/{fileCabinetId}/Documents" +
        $"?start=0&count={count}&calculateTotalCount=true";

    /// <summary>
    /// Liefert eine Seite Dokumente samt Folge-Link. Per HATEOAS-"next"-Link kann
    /// stabil weitergeblättert werden (robuster als start/count bei Änderungen).
    /// </summary>
    public async Task<DocumentPage> GetDocumentsAsync(string url, CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(
            () => JsonGet(url), HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = new List<DwDocument>();
        if (root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("Id", out var idEl))
                    continue;
                var id = idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetInt64().ToString()
                    : idEl.GetString() ?? "";
                if (string.IsNullOrEmpty(id))
                    continue;

                var fields = new Dictionary<string, string?>();
                if (item.TryGetProperty("Fields", out var fieldArr) &&
                    fieldArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in fieldArr.EnumerateArray())
                    {
                        var name = TryGetString(f, "FieldName");
                        if (string.IsNullOrEmpty(name))
                            continue;
                        fields[name] = ExtractFieldValue(f);
                    }
                }

                result.Add(new DwDocument(id, fields));
            }
        }

        var next = ExtractNextLink(root);
        return new DocumentPage(result, next);
    }

    /// <summary>Lädt die Indexfelder eines einzelnen Dokuments (für erneuten Versuch).</summary>
    public async Task<DwDocument> GetDocumentAsync(string fileCabinetId, string docId, CancellationToken ct)
    {
        var url = $"{PlatformBaseUrl}/FileCabinets/{fileCabinetId}/Documents/{docId}";
        using var resp = await SendWithRetryAsync(
            () => JsonGet(url), HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var fields = new Dictionary<string, string?>();
        if (root.TryGetProperty("Fields", out var fieldArr) && fieldArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fieldArr.EnumerateArray())
            {
                var name = TryGetString(f, "FieldName");
                if (!string.IsNullOrEmpty(name))
                    fields[name] = ExtractFieldValue(f);
            }
        }
        return new DwDocument(docId, fields);
    }

    /// <summary>Öffnet den Originalformat-Download eines Dokuments (gestreamt).</summary>
    public Task<DownloadStream> OpenDocumentDownloadAsync(string fileCabinetId, string docId, CancellationToken ct)
    {
        var url = $"{PlatformBaseUrl}/FileCabinets/{fileCabinetId}/Documents/{docId}/FileDownload" + DownloadQuery();
        return OpenDownloadAsync(url, $"document_{docId}", ct);
    }

    /// <summary>Liefert die Sektions-IDs eines Dokuments (für DownloadPerSection).</summary>
    public async Task<List<string>> GetSectionIdsAsync(string fileCabinetId, string docId, CancellationToken ct)
    {
        var url = $"{PlatformBaseUrl}/FileCabinets/{fileCabinetId}/Documents/{docId}/Sections";
        using var resp = await SendWithRetryAsync(
            () => JsonGet(url), HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var ids = new List<string>();
        if (doc.RootElement.TryGetProperty("Section", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in arr.EnumerateArray())
            {
                var id = TryGetString(s, "Id");
                if (!string.IsNullOrEmpty(id))
                    ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>Öffnet den Originalformat-Download einer Sektion (gestreamt).</summary>
    public Task<DownloadStream> OpenSectionDownloadAsync(string fileCabinetId, string sectionId, CancellationToken ct)
    {
        // Korrekter Endpunkt inkl. FileCabinet-Kontext (ohne diesen liefert die API 404).
        var url = $"{PlatformBaseUrl}/FileCabinets/{fileCabinetId}/Sections/{sectionId}/Data" + DownloadQuery();
        return OpenDownloadAsync(url, $"section_{sectionId}", ct);
    }

    /// <summary>Gemeinsame Download-Logik: Antwort-Stream + Dateiname aus Content-Disposition.</summary>
    private async Task<DownloadStream> OpenDownloadAsync(string url, string fallbackName, CancellationToken ct)
    {
        var resp = await SendWithRetryAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Get, url);
            r.Headers.Accept.ParseAdd("application/octet-stream");
            r.Headers.Accept.ParseAdd("*/*");
            return r;
        }, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        try
        {
            resp.EnsureSuccessStatusCode();
            var fileName = GetFileNameFromContentDisposition(resp) ?? fallbackName;
            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new DownloadStream(resp, fileName, stream);
        }
        catch
        {
            resp.Dispose();
            throw;
        }
    }

    // =====================================================================
    //  Hilfsfunktionen
    // =====================================================================

    private static HttpRequestMessage JsonGet(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        return req;
    }

    /// <summary>Sucht im Antwort-Objekt nach dem HATEOAS-Link mit rel="next" und löst ihn auf.</summary>
    private string? ExtractNextLink(JsonElement root)
    {
        if (!root.TryGetProperty("Links", out var links) || links.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var link in links.EnumerateArray())
        {
            var rel = TryGetString(link, "rel");
            if (!string.Equals(rel, "next", StringComparison.OrdinalIgnoreCase))
                continue;

            var href = TryGetString(link, "href");
            if (string.IsNullOrWhiteSpace(href))
                return null;

            // Absolute URL direkt nutzen, relative gegen die Server-Wurzel auflösen.
            if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return href;
            return _serverRoot + (href.StartsWith("/") ? href : "/" + href);
        }
        return null;
    }

    private void ClearCookies()
    {
        try
        {
            var uri = new Uri(PlatformBaseUrl);
            foreach (Cookie c in _cookies.GetCookies(uri))
                c.Expired = true;
        }
        catch { /* ignorieren */ }
    }

    /// <summary>Liest den Dateinamen aus Content-Disposition (filename* oder filename).</summary>
    private static string? GetFileNameFromContentDisposition(HttpResponseMessage resp)
    {
        var cd = resp.Content.Headers.ContentDisposition;
        if (cd != null)
        {
            var name = cd.FileNameStar ?? cd.FileName;
            if (!string.IsNullOrWhiteSpace(name))
                return name.Trim('"');
        }

        if (resp.Content.Headers.TryGetValues("Content-Disposition", out var values))
        {
            foreach (var v in values)
            {
                var idx = v.IndexOf("filename=", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var name = v[(idx + 9)..].Trim().Trim('"', ';', ' ');
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }
            }
        }
        return null;
    }

    /// <summary>Extrahiert den darstellbaren Wert eines Indexfelds (Item/ItemElementName).</summary>
    private static string? ExtractFieldValue(JsonElement field)
    {
        if (field.TryGetProperty("Item", out var item))
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.String:
                    return item.GetString();
                case JsonValueKind.Number:
                    return item.GetRawText();
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return item.GetBoolean().ToString();
                case JsonValueKind.Null:
                    return null;
                default:
                    return item.GetRawText();
            }
        }
        return null;
    }

    private static string? TryGetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp)
    {
        try { return await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }
}

/// <summary>Ein Dokument mit DocId und seinen Indexfeldern (Name -&gt; Wert).</summary>
public sealed record DwDocument(string DocId, Dictionary<string, string?> Fields);
