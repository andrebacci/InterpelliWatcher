# Guida: da Utilità di pianificazione a GitHub Actions

Questa guida ti porta dal progetto che gira sul tuo PC a un repository GitHub che
esegue il controllo degli interpelli da solo, senza che il computer sia acceso.

---

## 0. Prima di tutto: il token è compromesso

Nel progetto che mi hai mandato il token del bot Telegram è scritto in chiaro in due
posti: `appsettings.json` e `utility.txt`. Va rigenerato **prima** di pubblicare
qualsiasi cosa, perché con quel token chiunque può inviare messaggi come se fosse il
tuo bot.

1. Apri Telegram e scrivi a **@BotFather**.
2. Manda `/revoke`, scegli il bot: BotFather invalida il vecchio token e te ne dà uno nuovo.
3. Tieni da parte il nuovo token, ti serve al punto 5.

Il vecchio token da quel momento non funziona più, quindi non importa se resta in giro
in vecchie copie del progetto sul tuo disco.

> Nel progetto riorganizzato che trovi nello zip, `utility.txt` **non c'è più** e
> `appsettings.json` ha i campi dei segreti vuoti.

---

## 1. Il problema vero: dove finisce lo stato

Questa è la parte che rende il passaggio meno banale di "copio i file su GitHub".

Il programma funziona confrontando le righe di oggi con quelle salvate in
`state_*.json`. Sul tuo PC quei file restano nella cartella `publish` tra
un'esecuzione e l'altra. I runner di GitHub Actions invece sono **macchine usa e
getta**: vengono create, eseguono il job e vengono distrutte. Senza accorgimenti,
ogni esecuzione sarebbe una "prima esecuzione" e non notificheresti mai niente.

La soluzione adottata qui è la più semplice e affidabile: **i file di stato vivono nel
repository stesso**, nella cartella `state/`. Alla fine di ogni esecuzione il workflow
committa automaticamente i file aggiornati. Vantaggi: sopravvivono per sempre, sono
ispezionabili, e hai lo storico di cosa è cambiato e quando.

(L'alternativa, `actions/cache`, non va bene: la cache di GitHub viene sfrattata dopo
7 giorni di inattività e le chiavi sono immutabili.)

---

## 2. Cosa ho modificato nel progetto

Riepilogo delle modifiche, così sai cosa stai caricando.

**`InterpelliWatcher.csproj`**
- `OutputType` da `WinExe` a `Exe`. Serve un'applicazione console vera, altrimenti
  l'output non finisce nel log del workflow.
- `appsettings.local.json` viene copiato in output solo se esiste.

**`Program.cs`**
- La configurazione ora si legge a tre livelli, dal generico allo specifico:
  1. `appsettings.json` — versionato, **senza segreti**
  2. `appsettings.local.json` — opzionale, ignorato da git, per il tuo PC
  3. variabili d'ambiente — è il canale usato dai Secrets di GitHub Actions
- Nuove variabili d'ambiente riconosciute: `TELEGRAM_BOT_TOKEN`, `TELEGRAM_CHAT_IDS`
  (separati da virgola), `TELEGRAM_CHAT_ID_FRA`, `FRA_COMUNI`, `STATE_DIR`,
  `NOTIFY_WHEN_NO_NEWS`, `LOG_FILE`.
- `STATE_DIR` sposta i file di stato in una cartella a scelta (su Actions:
  `<repo>/state`). Senza questa variabile il comportamento resta quello di prima,
  cioè i file accanto all'eseguibile.
- `LOG_FILE=off` disattiva `log.txt`: su Actions il log lo tiene già il runner, e un
  `log.txt` che cambia a ogni giro sporcherebbe i commit.
- `NotifyWhenNoNews`: il messaggio "Nessun nuovo interpello trovato" ora è opzionale.
  Sul workflow è **disattivato** — con un controllo ogni 15 minuti sarebbero 96
  notifiche al giorno.
- I log usano l'ora italiana anche quando girano su un runner in UTC.
- L'elenco dei comuni "di Fra" era codificato nel sorgente: ora sta in
  `appsettings.json` (con l'elenco precedente come default).

**File nuovi**
- `.github/workflows/interpelli.yml` — il workflow.
- `.gitignore` — esclude `bin/`, `obj/`, `appsettings.local.json`, `utility.txt`, `log.txt`.
- `state/` — con i tuoi file di stato attuali già dentro, così non riparte da zero.
- `appsettings.local.json.esempio` — modello da copiare per l'uso locale.

**File rimossi**: `utility.txt`, e tutto il contenuto di `bin/` e `obj/` (non va mai
versionato).

---

## 3. Preparare il computer

Ti serve **Git**. Verifica dal Prompt dei comandi:

```
git --version
```

Se dà errore, scarica Git da <https://git-scm.com/download/win> e installalo lasciando
le opzioni predefinite. Alla prima installazione configura nome ed email:

```
git config --global user.name "Tuo Nome"
git config --global user.email "tua@email.it"
```

Ti serve anche un account su <https://github.com> (gratuito).

---

## 4. Creare il repository e caricare il progetto

### 4.1 Scegli pubblico o privato

Leggi prima il punto 8 sui costi: in sintesi, **con esecuzioni ogni 15 minuti ti
conviene un repository pubblico**, altrimenti finisci i minuti gratuiti in pochi
giorni. Nel repository non ci sono segreti (stanno nei Secrets, che restano privati
anche nei repo pubblici) e i file di stato contengono solo interpelli già pubblicati
sui siti del Ministero.

### 4.2 Crea il repository su GitHub

1. Vai su <https://github.com/new>.
2. **Repository name**: `InterpelliWatcher`.
3. Scegli **Public** (o Private, se hai letto il punto 8 e accetti di abbassare la frequenza).
4. **Non** spuntare "Add a README file" né altre inizializzazioni.
5. **Create repository**.

### 4.3 Carica i file

Scompatta lo zip che ti ho preparato in una cartella, per esempio
`C:\Progetti\InterpelliWatcher`. Poi apri il Prompt dei comandi lì dentro:

```
cd C:\Progetti\InterpelliWatcher
git init
git add .
git commit -m "Primo commit: watcher interpelli con GitHub Actions"
git branch -M main
git remote add origin https://github.com/TUO-UTENTE/InterpelliWatcher.git
git push -u origin main
```

Sostituisci `TUO-UTENTE` con il tuo nome utente GitHub. Al `push` si aprirà una
finestra per autenticarti con il browser: accetta.

Prima di premere invio sul `git add .`, controlla con `git status` che **non** compaiano
`appsettings.local.json`, `utility.txt`, `bin/` o `obj/`. Se compaiono, il `.gitignore`
non è nella cartella giusta (deve stare nella radice, accanto a `.github`).

---

## 5. Inserire i segreti su GitHub

I Secrets sono cifrati, non compaiono nel codice e vengono mascherati nei log.

1. Sul repository: **Settings** → menu a sinistra **Secrets and variables** → **Actions**.
2. Pulsante **New repository secret**, e crea questi tre:

| Name | Secret |
|---|---|
| `TELEGRAM_BOT_TOKEN` | il **nuovo** token del punto 0 |
| `TELEGRAM_CHAT_IDS` | `10963265` (più chat id separati da virgola, se servono) |
| `TELEGRAM_CHAT_ID_FRA` | `1462519079` |

Attenzione a non lasciare spazi o a capo alla fine del valore incollato.

---

## 6. Dare al workflow il permesso di scrivere

Il workflow deve poter ricommittare i file di stato.

1. **Settings** → **Actions** → **General**.
2. Scorri fino a **Workflow permissions**.
3. Seleziona **Read and write permissions**.
4. **Save**.

---

## 7. Primo test manuale

Non aspettare il cron: lancia il workflow a mano.

1. Vai sulla tab **Actions** del repository.
2. Se compare l'avviso "Workflows aren't being run on this forked repository" o un
   pulsante verde di abilitazione, abilita i workflow.
3. A sinistra clicca **Controllo interpelli**.
4. A destra **Run workflow** → **Run workflow**.
5. Dopo qualche secondo compare l'esecuzione: aprila e clicca sul job **controllo**.

Apri il passo **Esegui il controllo** e leggi il log. Dovresti vedere qualcosa come:

```
[2026-09-19 11:03:12] Cartella dei file di stato: /home/runner/work/InterpelliWatcher/InterpelliWatcher/state
[2026-09-19 11:03:12] Controllo pagina interpelli: https://...
[2026-09-19 11:03:14] OK, nessuna nuova riga (righe totali: 42).
```

Il pallino verde significa che ha funzionato. Se ci sono novità rispetto ai file di
stato che ho incluso, riceverai le notifiche Telegram subito.

Per una prova completa del canale Telegram: apri `state/state_liguria_2026_2027.json`
direttamente su GitHub, clicca la matita, cancella una o due righe dall'elenco,
committa, e rilancia il workflow. Il programma le vedrà come nuove e ti manderà i
messaggi.

---

## 8. Cose da sapere sul cron di GitHub (importanti)

**Il cron è in UTC e non è puntuale.** GitHub mette in coda i job schedulati e li
esegue quando ci sono runner liberi: ritardi di 5–20 minuti sono normali, e nei momenti
di picco un'esecuzione può saltare del tutto. Per questo nel workflow gli orari sono
sfalsati (`3,18,33,48`) invece dei minuti tondi, dove la coda è peggiore. Se ti serve
la puntualità al minuto, GitHub Actions non è lo strumento giusto.

**Minuti gratuiti.** Sui repository **pubblici** Actions è gratis e illimitato. Sui
repository **privati** il piano Free dà 2.000 minuti al mese e ogni esecuzione viene
arrotondata al minuto: 96 esecuzioni al giorno × ~2 minuti ≈ 5.800 minuti al mese, cioè
il triplo della quota. Quindi: repository pubblico, oppure repository privato con
frequenza molto più bassa (`0 */2 * * *`, ogni 2 ore, sta abbondantemente nella quota).

**Disattivazione dopo 60 giorni.** GitHub disabilita i workflow schedulati se il
repository resta senza attività per 60 giorni. I commit automatici dello stato
dovrebbero bastare a tenerlo vivo, ma se ricevi l'email "Scheduled workflow disabled"
ti basta riattivarlo dalla tab Actions.

**Se qualcosa fallisce** (sito irraggiungibile, errore Telegram) il job diventa rosso e
GitHub ti manda una email. È il tuo sistema di allerta gratuito: tienilo attivo.

---

## 9. Cambiare la frequenza o le pagine

**Frequenza**: modifica la riga `cron` in `.github/workflows/interpelli.yml`.
Ricorda che è UTC — irrilevante per gli intervalli ripetuti, importante se vuoi orari
fissi (l'Italia è UTC+1 d'inverno, UTC+2 d'estate).

| Obiettivo | cron |
|---|---|
| Ogni 15 minuti | `3,18,33,48 * * * *` |
| Ogni 30 minuti | `7,37 * * * *` |
| Ogni ora | `17 * * * *` |
| Ogni 2 ore | `17 */2 * * *` |
| Solo in orario d'ufficio (9–18 italiane, inverno) | `17 8-17 * * 1-5` |

**Pagine monitorate**: modifica `InterpelliWatcher/appsettings.json`. Se aggiungi una
pagina, dalle un `StateFile` nuovo: alla prima esecuzione il programma salverà lo stato
iniziale senza notificare nulla, come fa già sul tuo PC.

Puoi modificare questi file direttamente dall'interfaccia web di GitHub (matita
in alto a destra sul file) oppure in locale e poi `git add` / `git commit` / `git push`.

---

## 10. Continuare a usarlo anche sul PC

Le modifiche sono retrocompatibili: se non imposti nessuna variabile d'ambiente, il
programma si comporta come prima (stato e `log.txt` accanto all'eseguibile).

Per farlo girare in locale, nella cartella del progetto copia
`appsettings.local.json.esempio` in `appsettings.local.json`, metti dentro il nuovo
token e i chat id, e compila:

```
dotnet publish InterpelliWatcher\InterpelliWatcher.csproj -c Release -r win-x64 --self-contained false -o publish
```

Un avvertimento: `OutputType` ora è `Exe`, quindi lanciando l'exe compare una finestra
console. Se tieni attiva anche l'attività pianificata, nelle proprietà dell'attività
scegli **"Esegui indipendentemente dalla connessione dell'utente"** e la finestra
resta nascosta.

**Non far girare i due sistemi in parallelo sulle stesse pagine**: hanno file di stato
separati e ti arriverebbero notifiche doppie. Quando GitHub Actions funziona,
disattiva l'attività in Utilità di pianificazione (tasto destro → Disattiva).

---

## 11. Se qualcosa non va

| Sintomo | Causa probabile |
|---|---|
| Job rosso, log: "token o chat id Telegram mancanti" | Secrets non creati, o nomi scritti diversamente (devono essere esattamente `TELEGRAM_BOT_TOKEN` e `TELEGRAM_CHAT_IDS`) |
| Job rosso sul passo "Salva lo stato": `permission denied` | Manca il **Read and write permissions** del punto 6 |
| Nessuna notifica mai | Il bot non è mai stato avviato da quel chat id, oppure lo stato è già allineato (normale) |
| Notifiche di tutti gli interpelli come se fossero nuovi | I file in `state/` sono stati cancellati o svuotati |
| "nessuna riga trovata nella tabella" | La struttura HTML del sito è cambiata: va aggiornato `ExtractRows` |
| Il workflow non parte mai da solo | Actions disabilitate, oppure workflow schedulato disattivato per inattività (punto 8) |
| Errore `Invalid workflow file` | Indentazione YAML rovinata da un copia-incolla: lo YAML non tollera le tabulazioni |

Il log completo di ogni esecuzione resta nella tab **Actions** per 90 giorni.
