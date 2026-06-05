using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DwDocExport;

/// <summary>
/// Ergebnis eines Datei-Downloads: Rohbytes plus aus Content-Disposition
/// ermittelter Original-Dateiname.
/// </summary>
public sealed record DownloadResult(byte[] Data, string FileName);

/// <summary>Kurzinfo zu einem Dateischrank (Aktenschrank) – ohne Baskets.</summary>
public sealed record FileCabinetInfo(string Id, string Name);

/// <summary>
/// Kapselt sämtliche Kommunikation mit der DocuWare Platform REST API.
/// Unterstützt sowohl Cookie-Login (klassisch / On-Prem) als auch
/// Token-Login über den DocuWare Identity Service (Cloud / modernes On-Prem).
/// Dateityp-neutral: Downloads erfolgen im Originalformat.
/// </summary>
public sealed class DocuWareClient : IDisposable
{
    private readonly ExporterOptions _opt;
    private readonly Action<string>? _log;

    private readonly CookieContainer _cookies = new();
    private readonly HttpClientHandler _handler;
    private readonly HttpClient _http;

    // Aktiver Authentifizierungszustand
    private AuthMode _effectiveMode = AuthMode.Cookie;
    private string? _accessToken;
    private string? _refreshToken;
    private string? _tokenEndpoint;
    private DateTimeOffset _tokenExpiresUtc = DateTimeOffset.MinValue;

    // Konstante Parameter für den DocuWare Identity Service (Resource Owner Password Grant).
    // Verifiziert gegen DocuWare-Doku (KBA-37505 / developer.docuware.com OAuth Support).
    private const string DwClientId = "docuware.platform.net.client";
    private const string DwScope = "docuware.platform offline_access";

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

        _http = new HttpClient(_handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DwDocExport/1.0");

        var server = (_opt.Server ?? string.Empty).TrimEnd('/');
        PlatformBaseUrl = $"{server}/DocuWare/Platform";
    }

    private void Log(string msg) => _log?.Invoke(msg);

    // =====================================================================
    //  Authentifizierung
    // =====================================================================

    /// <summary>
    /// Authentifiziert gemäß <see cref="ExporterOptions.AuthMode"/>.
    /// Auto: zuerst Token, bei Fehlschlag Cookie.
    /// </summary>
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

    /// <summary>
    /// Klassischer Cookie-Login (v. a. On-Prem):
    /// POST {Platform}/Account/Logon mit Formularfeldern; Cookies bleiben im Handler.
    /// </summary>
    private async Task AuthenticateCookieAsync(CancellationToken ct)
    {
        // Vor erneutem Login alte Cookies verwerfen.
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

    /// <summary>
    /// Token-Login über den DocuWare Identity Service:
    /// 1) IdentityServiceInfo -> Identity-Service-URL
    /// 2) openid-configuration -> token_endpoint
    /// 3) Resource Owner Password Grant -> access_token (+ refresh_token)
    /// </summary>
    private async Task AuthenticateTokenAsync(CancellationToken ct)
    {
        ClearCookies();

        var identityServiceUrl = await GetIdentityServiceUrlAsync(ct).ConfigureAwait(false);
        _tokenEndpoint = await GetTokenEndpointAsync(identityServiceUrl, ct).ConfigureAwait(false);

        await RequestTokenWithPasswordAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ermittelt die URL des zuständigen Identity Service. DocuWare stellt diese
    /// je nach Version unter {Platform}/Home/IdentityServiceInfo (gängig) oder
    /// {Platform}/Account/IdentityServiceInfo bereit. Beide Pfade werden probiert;
    /// schlägt alles fehl =&gt; kein Token-Login möglich (Fallback auf Cookie).
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

                // Feldname je nach Version "IdentityServiceUrl" oder "identityServiceUrl".
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

    /// <summary>
    /// Liest den OpenID-Connect-Discovery-Endpunkt und liefert den token_endpoint.
    /// </summary>
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

        return endpoint;
    }

    /// <summary>
    /// Fordert per Resource Owner Password Grant ein Access-Token an.
    /// Die Organisation wird – sofern angegeben – über acr_values übergeben.
    /// </summary>
    private async Task RequestTokenWithPasswordAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_tokenEndpoint))
            throw new InvalidOperationException("token_endpoint ist nicht gesetzt.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["scope"] = DwScope,
            ["client_id"] = DwClientId,
            ["username"] = _opt.User,
            ["password"] = _opt.Password
        };

        // Organisation: DocuWare erwartet sie bei mehreren Organisationen.
        // Übergabe als acr_values (organization:<Name>) gemäß IdentityServer-Konvention.
        if (!string.IsNullOrWhiteSpace(_opt.Organization))
            form["acr_values"] = $"organization:{_opt.Organization}";

        using var content = new FormUrlEncodedContent(form);
        using var req = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint) { Content = content };
        req.Headers.Accept.ParseAdd("application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Token-Anforderung fehlgeschlagen (HTTP {(int)resp.StatusCode}). {body}");

        ApplyTokenResponse(body);
    }

    /// <summary>Erneuert das Access-Token über das refresh_token.</summary>
    private async Task RefreshTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_tokenEndpoint) || string.IsNullOrEmpty(_refreshToken))
        {
            // Kein Refresh möglich -> komplette Neuanmeldung.
            await RequestTokenWithPasswordAsync(ct).ConfigureAwait(false);
            return;
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = DwClientId,
            ["refresh_token"] = _refreshToken
        };

        using var content = new FormUrlEncodedContent(form);
        using var req = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint) { Content = content };
        req.Headers.Accept.ParseAdd("application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // Refresh fehlgeschlagen -> erneut mit Passwort anmelden.
            await RequestTokenWithPasswordAsync(ct).ConfigureAwait(false);
            return;
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ApplyTokenResponse(body);
    }

    /// <summary>Übernimmt access_token / refresh_token / expires_in aus der Token-Antwort.</summary>
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

        // Etwas Puffer vor Ablauf einplanen.
        _tokenExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    /// <summary>Stellt sicher, dass im Token-Modus ein gültiges Token vorliegt.</summary>
    private async Task EnsureTokenFreshAsync(CancellationToken ct)
    {
        if (_effectiveMode != AuthMode.Token)
            return;
        if (DateTimeOffset.UtcNow < _tokenExpiresUtc && _accessToken != null)
            return;

        await RefreshTokenAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Erneute Anmeldung (nach 401) im aktuell aktiven Modus.</summary>
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
    /// 401 -&gt; neu authentifizieren und einmal erneut; 429/5xx -&gt; Backoff (MaxRetries).
    /// Der Aufrufer liefert für jeden Versuch eine frische Request-Instanz.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct)
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
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
            }

            // 401: einmalig neu anmelden und Request wiederholen.
            if (resp.StatusCode == HttpStatusCode.Unauthorized && !reauthDone)
            {
                resp.Dispose();
                reauthDone = true;
                Log("HTTP 401 – erneute Authentifizierung.");
                await ReAuthenticateAsync(ct).ConfigureAwait(false);
                continue;
            }

            // 429 / 5xx: mit exponentiellem Backoff wiederholen.
            var transient = resp.StatusCode == (HttpStatusCode)429 || (int)resp.StatusCode >= 500;
            if (transient && attempt < maxRetries)
            {
                resp.Dispose();
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                Log($"Vorübergehender Fehler – erneuter Versuch in {delay.TotalSeconds:0}s.");
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            return resp;
        }
    }

    // =====================================================================
    //  Plattform-Operationen
    // =====================================================================

    /// <summary>Liefert alle Dateischränke (echte Aktenschränke, keine Baskets).</summary>
    public async Task<List<FileCabinetInfo>> GetFileCabinetsAsync(CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(
            () => JsonGet($"{PlatformBaseUrl}/FileCabinets"), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var list = new List<FileCabinetInfo>();
        if (doc.RootElement.TryGetProperty("FileCabinet", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var fc in arr.EnumerateArray())
            {
                // Baskets ausschließen (IsBasket == true).
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

    /// <summary>Liefert die Indexfeld-Namen (DBName) eines Schranks.</summary>
    public async Task<List<string>> GetFieldNamesAsync(string fileCabinetId, CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(
            () => JsonGet($"{PlatformBaseUrl}/FileCabinets/{fileCabinetId}"), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var names = new List<string>();
        if (doc.RootElement.TryGetProperty("Fields", out var fields) &&
            fields.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fields.EnumerateArray())
            {
                var dbName = TryGetString(f, "DBName") ?? TryGetString(f, "DbName");
                if (!string.IsNullOrEmpty(dbName))
                    names.Add(dbName);
            }
        }
        return names;
    }

    /// <summary>Liefert die Gesamtzahl der Dokumente im Schrank (Count=0 + calculateTotalCount).</summary>
    public async Task<int> GetDocumentCountAsync(string fileCabinetId, CancellationToken ct)
    {
        var url = $"{PlatformBaseUrl}/FileCabinets/{fileCabinetId}/Documents?start=0&count=1&calculateTotalCount=true";
        using var resp = await SendWithRetryAsync(() => JsonGet(url), ct).ConfigureAwait(false);
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

    /// <summary>
    /// Liefert eine Seite von Dokumenten. Jedes Element enthält DocId und die
    /// Indexfelder als Schlüssel/Wert-Liste.
    /// </summary>
    public async Task<List<DwDocument>> GetPageAsync(string fileCabinetId, int start, int count, CancellationToken ct)
    {
        var url = $"{PlatformBaseUrl}/FileCabinets/{fileCabinetId}/Documents" +
                  $"?start={start}&count={count}&calculateTotalCount=true";

        using var resp = await SendWithRetryAsync(() => JsonGet(url), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var result = new List<DwDocument>();
        if (doc.RootElement.TryGetProperty("Items", out var items) &&
            items.ValueKind == JsonValueKind.Array)
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
        return result;
    }

    /// <summary>
    /// Lädt ein Dokument im Originalformat herunter (targetFileType=Auto).
    /// Der Dateiname wird aus dem Content-Disposition-Header ermittelt.
    /// </summary>
    public async Task<DownloadResult> DownloadDocumentAsync(string fileCabinetId, string docId, CancellationToken ct)
    {
        var url = $"{PlatformBaseUrl}/FileCabinets/{fileCabinetId}/Documents/{docId}/FileDownload" +
                  "?targetFileType=Auto&keepAnnotations=false";

        using var resp = await SendWithRetryAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Get, url);
            // Beliebiger Inhaltstyp – wir wollen die Originaldatei.
            r.Headers.Accept.ParseAdd("application/octet-stream");
            r.Headers.Accept.ParseAdd("*/*");
            return r;
        }, ct).ConfigureAwait(false);

        resp.EnsureSuccessStatusCode();

        var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var fileName = GetFileNameFromContentDisposition(resp) ?? $"document_{docId}";
        return new DownloadResult(data, fileName);
    }

    /// <summary>Liefert die Sektions-IDs eines Dokuments (für DownloadPerSection).</summary>
    public async Task<List<string>> GetSectionIdsAsync(string fileCabinetId, string docId, CancellationToken ct)
    {
        var url = $"{PlatformBaseUrl}/FileCabinets/{fileCabinetId}/Documents/{docId}/Sections";
        using var resp = await SendWithRetryAsync(() => JsonGet(url), ct).ConfigureAwait(false);
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

    /// <summary>Lädt die Daten einer einzelnen Sektion im Originalformat herunter.</summary>
    public async Task<DownloadResult> DownloadSectionAsync(string sectionId, CancellationToken ct)
    {
        var url = $"{PlatformBaseUrl}/Sections/{sectionId}/Data?targetFileType=Auto&keepAnnotations=false";
        using var resp = await SendWithRetryAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Get, url);
            r.Headers.Accept.ParseAdd("application/octet-stream");
            r.Headers.Accept.ParseAdd("*/*");
            return r;
        }, ct).ConfigureAwait(false);

        resp.EnsureSuccessStatusCode();

        var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var fileName = GetFileNameFromContentDisposition(resp) ?? $"section_{sectionId}";
        return new DownloadResult(data, fileName);
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

        // Manche Server liefern den Header nicht typisiert – manuell parsen.
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
