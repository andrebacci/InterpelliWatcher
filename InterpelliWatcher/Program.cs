using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using HtmlAgilityPack;

namespace InterpelliWatcher;

internal class PageConfig
{
    public string PageUrl { get; set; } = "";
    public string StateFile { get; set; } = "state.json";
    public string? SiteName { get; set; }

    /// <summary>Nome del sito da mostrare nel messaggio: quello configurato, o l'host dell'URL come fallback.</summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(SiteName)
        ? SiteName
        : Uri.TryCreate(PageUrl, UriKind.Absolute, out var uri) ? uri.Host : PageUrl;
}

internal class Config
{
    public List<PageConfig> Pages { get; set; } = new();

    /// <summary>Segreto: NON va scritto in appsettings.json versionato. Usa la variabile d'ambiente TELEGRAM_BOT_TOKEN (o appsettings.local.json).</summary>
    public string TelegramBotToken { get; set; } = "";

    /// <summary>Segreto: variabile d'ambiente TELEGRAM_CHAT_IDS (separati da virgola) o appsettings.local.json.</summary>
    public List<string> TelegramChatIds { get; set; } = new();

    /// <summary>Segreto: variabile d'ambiente TELEGRAM_CHAT_ID_FRA o appsettings.local.json.</summary>
    public string TelegramChatIdFra { get; set; } = "";

    /// <summary>Comuni per i quali si aggiunge il destinatario "Fra". Se vuoto si usa l'elenco predefinito.</summary>
    public List<string> FraComuni { get; set; } = new();

    /// <summary>
    /// Cartella dove salvare i file di stato. Vuota = accanto all'eseguibile (comportamento storico).
    /// Su GitHub Actions viene impostata a &lt;repo&gt;/state tramite la variabile d'ambiente STATE_DIR.
    /// </summary>
    public string StateDir { get; set; } = "";

    /// <summary>Se true invia un messaggio Telegram anche quando non c'è nulla di nuovo. Su GitHub Actions conviene false.</summary>
    public bool NotifyWhenNoNews { get; set; } = true;
}

/// <summary>
/// Rappresenta una riga della tabella con le celle separate, in modo da poter
/// isolare il nome della scuola (colonna "Denominazione") nel messaggio Telegram.
/// L'ordine delle colonne nella pagina è:
/// CDC | Comune/Sostegno | CM | Denominazione | Dal | Al | Tipo cattedra | Intera/Ore spezzone | Disponibilità | Termine presentazione domanda | Link
/// </summary>
internal class InterpelloRow
{
    private const int DenominazioneIndex = 3;

    public string[] Cells { get; }

    /// <summary>Stringa unica usata come chiave per il confronto con lo stato precedente.</summary>
    public string Key { get; }

    private readonly int _schoolNameIndex;

    public InterpelloRow(string[] cells, int schoolNameIndex = DenominazioneIndex)
    {
        Cells = cells;
        Key = string.Join(" | ", cells);
        _schoolNameIndex = schoolNameIndex;
    }

    /// <summary>Nome della scuola (colonna "Denominazione"), o l'intera riga se la colonna non è presente.</summary>
    public string SchoolName => Cells.Length > _schoolNameIndex ? Cells[_schoolNameIndex] : Key;

    /// <summary>Il resto dei dettagli della riga, senza ripetere il nome della scuola.</summary>
    public string DetailsWithoutSchoolName =>
        string.Join(" | ", Cells.Where((c, i) => i != _schoolNameIndex));
}

internal class Program
{
    /// <summary>Elenco usato se in configurazione non è specificato nulla.</summary>
    private static readonly string[] DefaultFraComuni =
    {
        "Chiavari", "Carasco", "Lavagna", "Sestri Levante", "Zoagli", "Casarza"
    };

    /// <summary>
    /// I runner di GitHub Actions lavorano in UTC: forziamo l'ora italiana nei log
    /// così restano leggibili come quando il programma gira sul PC.
    /// </summary>
    private static readonly TimeZoneInfo ItalianTimeZone = ResolveItalianTimeZone();

    private static TimeZoneInfo ResolveItalianTimeZone()
    {
        foreach (var id in new[] { "Europe/Rome", "W. Europe Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Local;
    }

    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        // Alcuni ambienti (es. Windows Server datati) negoziano di default protocolli TLS
        // non più accettati da Telegram: forziamo esplicitamente TLS 1.2/1.3.
        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
    });

    /// <summary>null = log solo su console (è il caso di GitHub Actions, dove il log lo tiene già il runner).</summary>
    private static string? _logFilePath;

    private static async Task<int> Main()
    {
        _logFilePath = ResolveLogFilePath();

        try
        {
            var config = LoadConfig();

            if (string.IsNullOrWhiteSpace(config.TelegramBotToken) ||
                config.TelegramBotToken.StartsWith("INSERISCI") ||
                config.TelegramChatIds == null ||
                config.TelegramChatIds.Count == 0 ||
                config.TelegramChatIds.Any(id => string.IsNullOrWhiteSpace(id) || id.StartsWith("INSERISCI")))
            {
                
               Log("ERRORE: token o chat id Telegram mancanti. Imposta i Secrets TELEGRAM_BOT_TOKEN e TELEGRAM_CHAT_IDS " +
                    "(su GitHub: Settings > Secrets and variables > Actions) oppure valorizzali in appsettings.local.json."); 
                    //return 1;
            }

            if (config.Pages == null || config.Pages.Count == 0)
            {
                Log("ERRORE: nessuna pagina configurata in appsettings.json (sezione Pages).");
                return 1;
            }

            var anyNewRows = false;
            var allPagesAvailable = true;

            foreach (var page in config.Pages)
            {
                Log($"Controllo pagina interpelli: {page.PageUrl}");
                //--await SendTelegramMessageToAllAsync(config.TelegramBotToken, config.TelegramChatIds, $"Controllo pagina interpelli: {page.PageUrl}");

                string html;
                try
                {
                    html = await FetchPageAsync(page.PageUrl);
                }
                catch (HttpRequestException ex)
                {
                    allPagesAvailable = false;
                    Log($"ATTENZIONE: impossibile raggiungere la pagina {page.PageUrl}: {ex.Message}. La pagina viene saltata; verrà ritentata al prossimo controllo.");
                    continue;
                }
                var currentRows = ExtractRows(html);

                if (currentRows.Count == 0)
                {
                    Log("ATTENZIONE: nessuna riga trovata nella tabella. La struttura della pagina potrebbe essere cambiata.");
                    continue;
                }

                var previousRows = LoadState(page.StateFile);

                if (previousRows == null)
                {
                    // Prima esecuzione: salviamo lo stato iniziale senza inviare notifiche,
                    // altrimenti riceveresti subito un messaggio con TUTTE le righe esistenti.
                    SaveState(page.StateFile, currentRows);
                    Log($"Prima esecuzione: salvate {currentRows.Count} righe come stato iniziale. Nessuna notifica inviata.");
                    continue;
                }

                var newRows = currentRows.Where(r => !previousRows.Contains(r.Key)).ToList();

                if (newRows.Count > 0)
                {
                    anyNewRows = true;
                    Log($"Trovate {newRows.Count} nuove righe. Invio una notifica Telegram per ogni riga a {config.TelegramChatIds.Count} destinatari...");
                    var allSent = true;

                    foreach (var row in newRows)
                    {
                        var message = BuildMessage(new List<InterpelloRow> { row }, page.PageUrl, page.DisplayName);
                        var recipients = config.TelegramChatIds.ToList();
                        var fraComuni = config.FraComuni.Count > 0 ? config.FraComuni.ToArray() : DefaultFraComuni;
                        if (!string.IsNullOrWhiteSpace(config.TelegramChatIdFra) &&
                            fraComuni.Any(comune => row.Key.Contains(comune, StringComparison.OrdinalIgnoreCase)) &&
                            !recipients.Contains(config.TelegramChatIdFra, StringComparer.OrdinalIgnoreCase))
                        {
                            recipients.Add(config.TelegramChatIdFra);
                            Log($"La riga contiene un comune di competenza: aggiunto il destinatario Telegram {config.TelegramChatIdFra}.");
                        }

                        var sent = await SendTelegramMessageToAllAsync(config.TelegramBotToken, recipients, message);
                        if (!sent)
                            allSent = false;
                    }

                    if (!allSent)
                    {
                        // Non aggiorniamo lo stato: le righe non notificate restano "nuove" e verranno
                        // rispedite (insieme a eventuali altre nuove righe) al prossimo tentativo riuscito.
                        Log("Invio fallito per almeno un destinatario: stato NON aggiornato, le righe verranno riproposte al prossimo controllo.");
                        continue;
                    }
                }
                else
                {
                    Log($"OK, nessuna nuova riga (righe totali: {currentRows.Count}).");
                    //await SendTelegramMessageToAllAsync(config.TelegramBotToken, config.TelegramChatIds, "Nessun nuovo interpello trovato.");
                }

                SaveState(page.StateFile, currentRows);
            }

            if (!anyNewRows && allPagesAvailable)
            {
                Log("Nessun nuovo interpello trovato.");
                if (config.NotifyWhenNoNews)
                    await SendTelegramMessageToAllAsync(config.TelegramBotToken, config.TelegramChatIds, "Nessun nuovo interpello trovato.");
            }
            else if (!allPagesAvailable)
            {
                Log("Controllo incompleto: nessuna notifica di assenza nuovi interpelli inviata.");
            }
            
            return 0;
        }
        catch (Exception ex)
        {
            Log($"ERRORE: {ex}");
            return 1;
        }
        finally
        {
            TrimLogFile();
        }
    }

    /// <summary>
    /// Scrive sia su console (utile per i test manuali) sia su log.txt (utile per Task Scheduler,
    /// dove la console non è visibile).
    /// </summary>
    private static void Log(string message)
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ItalianTimeZone);
        var line = $"[{now:yyyy-MM-dd HH:mm:ss}] {message}";
        Console.WriteLine(line);

        if (_logFilePath == null) return; // solo console (GitHub Actions)

        try
        {
            File.AppendAllText(_logFilePath, line + Environment.NewLine);
        }
        catch
        {
            // se il log su file fallisce (es. permessi) non blocchiamo l'esecuzione
        }
    }

    /// <summary>
    /// LOG_FILE non impostata  -> log.txt accanto all'eseguibile (come prima).
    /// LOG_FILE = "off"/"none" -> nessun file, solo console: è quello che usa GitHub Actions.
    /// LOG_FILE = percorso     -> quel file.
    /// </summary>
    private static string? ResolveLogFilePath()
    {
        var setting = Environment.GetEnvironmentVariable("LOG_FILE");

        if (string.IsNullOrWhiteSpace(setting))
            return Path.Combine(AppContext.BaseDirectory, "log.txt");

        setting = setting.Trim();
        if (setting.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            setting.Equals("none", StringComparison.OrdinalIgnoreCase))
            return null;

        return Path.IsPathRooted(setting) ? setting : Path.Combine(AppContext.BaseDirectory, setting);
    }

    /// <summary>
    /// Mantiene log.txt entro le ultime ~1000 righe, per non farlo crescere all'infinito
    /// dato che il task gira ogni 15 minuti.
    /// </summary>
    private static void TrimLogFile()
    {
        try
        {
            if (_logFilePath == null || !File.Exists(_logFilePath)) return;
            var lines = File.ReadAllLines(_logFilePath);
            if (lines.Length > 1000)
                File.WriteAllLines(_logFilePath, lines[^1000..]);
        }
        catch
        {
            // non critico
        }
    }

    /// <summary>
    /// La configurazione viene letta a livelli, dal più generico al più specifico:
    ///   1. appsettings.json          (versionato su GitHub, NON deve contenere segreti)
    ///   2. appsettings.local.json    (opzionale, ignorato da git: comodo sul PC di casa)
    ///   3. variabili d'ambiente      (è il canale usato dai Secrets di GitHub Actions)
    /// </summary>
    private static Config LoadConfig()
    {
        var baseDir = AppContext.BaseDirectory;

        var path = Path.Combine(baseDir, "appsettings.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("File appsettings.json non trovato accanto all'eseguibile.", path);

        var config = ReadConfigFile(path) ?? throw new Exception("appsettings.json non valido.");

        var localPath = Path.Combine(baseDir, "appsettings.local.json");
        if (File.Exists(localPath))
        {
            var local = ReadConfigFile(localPath);
            if (local != null)
            {
                ApplyOverrides(config, local);
                Log("Applicate le impostazioni di appsettings.local.json.");
            }
        }

        ApplyEnvironmentOverrides(config);
        ResolveStateFilePaths(config, baseDir);

        return config;
    }

    private static Config? ReadConfigFile(string path)
    {
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        if (config == null) return null;

        // Una chiave scritta esplicitamente come null nel JSON annullerebbe il valore di default
        config.Pages ??= new List<PageConfig>();
        config.FraComuni ??= new List<string>();
        config.TelegramChatIds ??= new List<string>();
        config.TelegramBotToken ??= "";
        config.TelegramChatIdFra ??= "";
        config.StateDir ??= "";

        return config;
    }

    /// <summary>Copia nel config di base solo i valori effettivamente valorizzati nel file di override.</summary>
    private static void ApplyOverrides(Config target, Config source)
    {
        if (source.Pages.Count > 0) target.Pages = source.Pages;
        if (source.FraComuni.Count > 0) target.FraComuni = source.FraComuni;
        if (source.TelegramChatIds.Count > 0) target.TelegramChatIds = source.TelegramChatIds;
        if (!string.IsNullOrWhiteSpace(source.TelegramBotToken)) target.TelegramBotToken = source.TelegramBotToken;
        if (!string.IsNullOrWhiteSpace(source.TelegramChatIdFra)) target.TelegramChatIdFra = source.TelegramChatIdFra;
        if (!string.IsNullOrWhiteSpace(source.StateDir)) target.StateDir = source.StateDir;
    }

    private static void ApplyEnvironmentOverrides(Config config)
    {
        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            config.TelegramBotToken = token.Trim();

        var chatIds = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_IDS");
        if (!string.IsNullOrWhiteSpace(chatIds))
            config.TelegramChatIds = SplitList(chatIds);

        var chatIdFra = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID_FRA");
        if (!string.IsNullOrWhiteSpace(chatIdFra))
            config.TelegramChatIdFra = chatIdFra.Trim();

        var fraComuni = Environment.GetEnvironmentVariable("FRA_COMUNI");
        if (!string.IsNullOrWhiteSpace(fraComuni))
            config.FraComuni = SplitList(fraComuni);

        var stateDir = Environment.GetEnvironmentVariable("STATE_DIR");
        if (!string.IsNullOrWhiteSpace(stateDir))
            config.StateDir = stateDir.Trim();

        var notify = Environment.GetEnvironmentVariable("NOTIFY_WHEN_NO_NEWS");
        if (!string.IsNullOrWhiteSpace(notify) && bool.TryParse(notify.Trim(), out var notifyValue))
            config.NotifyWhenNoNews = notifyValue;
    }

    private static List<string> SplitList(string value) =>
        value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .ToList();

    /// <summary>
    /// I percorsi relativi dei file di stato vengono risolti dentro StateDir (se impostata),
    /// altrimenti accanto all'eseguibile come nella versione originale.
    /// </summary>
    private static void ResolveStateFilePaths(Config config, string baseDir)
    {
        var stateBase = string.IsNullOrWhiteSpace(config.StateDir)
            ? baseDir
            : Path.GetFullPath(config.StateDir);

        Directory.CreateDirectory(stateBase);
        Log($"Cartella dei file di stato: {stateBase}");

        foreach (var page in config.Pages)
        {
            if (!Path.IsPathRooted(page.StateFile))
                page.StateFile = Path.Combine(stateBase, page.StateFile);
        }
    }

    private static async Task<string> FetchPageAsync(string url)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                // Alcuni siti della PA rifiutano richieste senza uno User-Agent "da browser"
                request.Headers.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");

                using var response = await HttpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                Log($"Tentativo {attempt}/{maxAttempts} fallito per {url}, riprovo...");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
        }

        throw new HttpRequestException($"Impossibile raggiungere {url} dopo {maxAttempts} tentativi.");
    }

    /// <summary>
    /// Estrae ogni riga degli interpelli con le celle separate, così da poter isolare in
    /// seguito il nome della scuola. Supporta sia le pagine con tabella HTML classica
    /// (istruzionegenova.gov.it) sia le pagine a "card" di servizi.istruzioneliguria.gov.it.
    /// </summary>
    private static List<InterpelloRow> ExtractRows(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var rows = ExtractRowsFromTable(doc);
        if (rows.Count > 0) return rows;

        return ExtractRowsFromResultCards(doc);
    }

    private static List<InterpelloRow> ExtractRowsFromTable(HtmlDocument doc)
    {
        var rows = new List<InterpelloRow>();

        var tables = doc.DocumentNode.SelectNodes("//table");
        if (tables == null) return rows;

        foreach (var table in tables)
        {
            // Individuiamo la tabella giusta cercando l'intestazione "CDC"
            if (!table.InnerText.Contains("CDC", StringComparison.OrdinalIgnoreCase))
                continue;

            var trNodes = table.SelectNodes(".//tr");
            if (trNodes == null) continue;

            foreach (var tr in trNodes)
            {
                var cells = tr.SelectNodes(".//td");
                if (cells == null || cells.Count == 0) continue; // salta le righe di intestazione (th)

                var cellTexts = cells
                    .Select(c => WebUtility.HtmlDecode(c.InnerText).Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToArray();

                if (cellTexts.Length > 0)
                    rows.Add(new InterpelloRow(cellTexts));
            }

            break; // trovata la tabella giusta, ci fermiamo
        }

        // Righe duplicate (stessa chiave) contano una sola volta
        return rows.GroupBy(r => r.Key).Select(g => g.First()).ToList();
    }

    /// <summary>
    /// Estrae le righe dalla pagina a "card" di servizi.istruzioneliguria.gov.it, la cui struttura è:
    /// div.results-table &gt; div.results-row &gt; span (Codice/Tipologia | Scuola | Ordine di scuola | Scadenza | Link).
    /// </summary>
    private static List<InterpelloRow> ExtractRowsFromResultCards(HtmlDocument doc)
    {
        var rows = new List<InterpelloRow>();

        var container = doc.DocumentNode.SelectSingleNode("//div[contains(concat(' ', normalize-space(@class), ' '), ' results-table ')]");
        var rowNodes = container?.SelectNodes(".//div[contains(concat(' ', normalize-space(@class), ' '), ' results-row ')]");
        if (rowNodes == null) return rows;

        foreach (var rowNode in rowNodes)
        {
            var spans = rowNode.SelectNodes("./span");
            if (spans == null || spans.Count < 4) continue;

            var codice = WebUtility.HtmlDecode(spans[0].SelectSingleNode(".//strong")?.InnerText ?? "").Trim();
            var tipologia = WebUtility.HtmlDecode(spans[0].SelectSingleNode(".//small")?.InnerText ?? "").Trim();

            var denominazione = WebUtility.HtmlDecode(spans[1].SelectSingleNode("./span")?.InnerText ?? spans[1].InnerText).Trim();
            var tipoScuola = WebUtility.HtmlDecode(spans[1].SelectSingleNode(".//small")?.InnerText ?? "").Trim();

            var ordineScuola = WebUtility.HtmlDecode(spans[2].InnerText).Trim();

            var scadenzaTextNode = spans[3].ChildNodes.FirstOrDefault(n => n.NodeType == HtmlNodeType.Text);
            var scadenza = WebUtility.HtmlDecode(scadenzaTextNode?.InnerText ?? "").Trim();
            var stato = WebUtility.HtmlDecode(spans[3].SelectSingleNode(".//span")?.InnerText ?? "").Trim();

            var link = spans.Count > 4 ? spans[4].SelectSingleNode(".//a")?.GetAttributeValue("href", "") ?? "" : "";

            var cellTexts = new[] { codice, tipologia, denominazione, tipoScuola, ordineScuola, scadenza, stato, link };
            if (!string.IsNullOrWhiteSpace(denominazione))
                rows.Add(new InterpelloRow(cellTexts, schoolNameIndex: 2));
        }

        // Righe duplicate (stessa chiave) contano una sola volta
        return rows.GroupBy(r => r.Key).Select(g => g.First()).ToList();
    }

    private static HashSet<string>? LoadState(string path)
    {
        if (!File.Exists(path)) return null;

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            // File vuoto/corrotto (es. scrittura interrotta): lo trattiamo come stato assente.
            Log($"ATTENZIONE: file di stato '{path}' vuoto o illeggibile, verr\u00e0 rigenerato senza inviare notifiche.");
            return null;
        }

        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(json);
            return list == null ? new HashSet<string>() : new HashSet<string>(list);
        }
        catch (JsonException ex)
        {
            Log($"ATTENZIONE: file di stato '{path}' corrotto ({ex.Message}), verr\u00e0 rigenerato senza inviare notifiche.");
            return null;
        }
    }

    private static void SaveState(string path, List<InterpelloRow> rows)
    {
        var keys = rows.Select(r => r.Key).ToList();
        var json = JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true });

        // Scriviamo su un file temporaneo e poi sostituiamo, per evitare che un'interruzione
        // a met\u00e0 scrittura lasci il file di stato vuoto/corrotto.
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Costruisce il messaggio Telegram in formato HTML, con il nome della scuola
    /// evidenziato in grassetto per ogni nuova riga.
    /// </summary>
    private static string BuildMessage(List<InterpelloRow> newRows, string pageUrl, string siteName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"\U0001F514 Nuovi interpelli pubblicati su {EscapeHtml(siteName)} ({newRows.Count}):");
        sb.AppendLine();

        foreach (var row in newRows.Take(10)) // evitiamo messaggi troppo lunghi
        {
            var splitted = row.Key.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            sb.AppendLine($"• <b>{EscapeHtml(row.SchoolName)}</b>");
            sb.AppendLine(EscapeHtml(row.DetailsWithoutSchoolName));

            if (splitted?.Length > 0)
                sb.AppendLine($"• {EscapeHtml(string.Concat("https://servizi.istruzioneliguria.gov.it/", splitted.Last()))}");

            sb.AppendLine();
        }

        if (newRows.Count > 10)
            sb.AppendLine($"... e altre {newRows.Count - 10} righe. Controlla la pagina per i dettagli.");

        sb.AppendLine(pageUrl);

        return sb.ToString();
    }

    /// <summary>Escape dei caratteri speciali richiesti da Telegram quando si usa parse_mode HTML.</summary>
    private static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static async Task<bool> SendTelegramMessageToAllAsync(string botToken, List<string> chatIds, string message)
    {
        var failures = new List<string>();

        foreach (var chatId in chatIds)
        {
            try
            {
                await SendTelegramMessageWithRetryAsync(botToken, chatId, message);
                Log($"Notifica Telegram inviata con successo a {chatId}.");
            }
            catch (Exception ex)
            {
                // Non blocchiamo l'invio agli altri destinatari se uno fallisce
                // (es. chat_id sbagliato, utente che ha bloccato il bot).
                Log($"ERRORE invio a {chatId}: {ex.Message}");
                failures.Add(chatId);
            }
        }

        if (failures.Count > 0)
            Log($"Invio fallito per {failures.Count}/{chatIds.Count} destinatari: {string.Join(", ", failures)}");

        return failures.Count == 0;
    }

    /// <summary>Ritenta l'invio in caso di errori di rete/SSL transitori (frequenti su alcuni Task Scheduler/reti).</summary>
    private static async Task SendTelegramMessageWithRetryAsync(string botToken, string chatId, string message)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await SendTelegramMessageAsync(botToken, chatId, message);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && (ex is HttpRequestException or AuthenticationException or IOException))
            {
                Log($"Tentativo {attempt}/{maxAttempts} fallito per {chatId} ({ex.GetType().Name}: {ex.Message}), riprovo...");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
        }
    }

    private static async Task SendTelegramMessageAsync(string botToken, string chatId, string message)
    {
        var url = $"https://api.telegram.org/bot{botToken}/sendMessage";

        // Telegram ha un limite di 4096 caratteri per messaggio
        if (message.Length > 4000)
            message = message[..4000] + "\n... (troncato)";

        var payload = new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["text"] = message,
            ["parse_mode"] = "HTML"
        };

        using var content = new FormUrlEncodedContent(payload);
        using var response = await HttpClient.PostAsync(url, content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Errore invio Telegram: {response.StatusCode} - {body}");
    }
}
