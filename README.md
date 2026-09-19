# InterpelliWatcher

Controlla periodicamente le pagine degli interpelli della provincia di Genova e invia
una notifica Telegram quando compare una riga nuova.

Può girare in due modi:

- **su GitHub Actions**, in automatico ogni 15 minuti, senza bisogno che il PC sia acceso;
- **sul PC**, tramite Utilità di pianificazione di Windows.

Per la configurazione completa su GitHub vedi **[GUIDA-DEPLOY-GITHUB.md](GUIDA-DEPLOY-GITHUB.md)**.

## Struttura

```
.github/workflows/interpelli.yml   il workflow schedulato
InterpelliWatcher/
  Program.cs                       tutta la logica
  appsettings.json                 pagine monitorate (nessun segreto qui dentro)
  appsettings.local.json.esempio   modello per l'esecuzione locale
state/                             file di stato, aggiornati e committati dal workflow
```

## Configurazione

I valori vengono letti a tre livelli, ognuno sovrascrive il precedente:

1. `InterpelliWatcher/appsettings.json` — versionato, **mai segreti qui**
2. `InterpelliWatcher/appsettings.local.json` — ignorato da git, per l'uso locale
3. variabili d'ambiente — usate dai Secrets di GitHub Actions

| Variabile d'ambiente | Corrisponde a | Note |
|---|---|---|
| `TELEGRAM_BOT_TOKEN` | `TelegramBotToken` | segreto |
| `TELEGRAM_CHAT_IDS` | `TelegramChatIds` | più valori separati da virgola |
| `TELEGRAM_CHAT_ID_FRA` | `TelegramChatIdFra` | destinatario aggiuntivo per certi comuni |
| `FRA_COMUNI` | `FraComuni` | comuni che attivano il destinatario aggiuntivo |
| `STATE_DIR` | `StateDir` | cartella dei file di stato; vuota = accanto all'eseguibile |
| `NOTIFY_WHEN_NO_NEWS` | `NotifyWhenNoNews` | `true`/`false` |
| `LOG_FILE` | — | percorso del log, oppure `off` per il solo output su console |

## Esecuzione locale

```powershell
copy InterpelliWatcher\appsettings.local.json.esempio InterpelliWatcher\appsettings.local.json
# compila appsettings.local.json con token e chat id, poi:
dotnet publish InterpelliWatcher\InterpelliWatcher.csproj -c Release -r win-x64 --self-contained false -o publish
cd publish
.\InterpelliWatcher.exe
```

Alla prima esecuzione su una pagina il programma salva le righe attuali come stato
iniziale e **non invia notifiche**, per non ricevere subito un messaggio con tutti gli
interpelli già presenti.

## Note sul funzionamento

- Il confronto è sul testo completo di ogni riga: se un valore cambia (es. il termine di
  presentazione), la riga viene trattata come nuova e rinotificata. È voluto.
- Se l'invio Telegram fallisce, lo stato **non** viene aggiornato: le righe restano
  "nuove" e vengono riproposte al controllo successivo.
- Se una pagina è irraggiungibile viene saltata e ritentata al giro dopo, senza
  perdere lo stato.
- Non cancellare i file in `state/`: al giro successivo riceveresti una notifica per
  ogni interpello esistente.
