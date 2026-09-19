#!/bin/bash
set -e

# ---------------------------------------------------------------------------
# Variabili obbligatorie (impostate come variabili d'ambiente su Railway):
#   TELEGRAM_BOT_TOKEN   - token del bot
#   TELEGRAM_CHAT_IDS    - chat id separati da virgola
#   TELEGRAM_CHAT_ID_FRA - chat id aggiuntivo (opzionale)
#   GH_TOKEN             - Personal Access Token GitHub con permesso "repo"
#   GH_REPO              - es. "andrebacci/InterpelliWatcher"
#   GH_BRANCH            - es. "main"
# ---------------------------------------------------------------------------

echo "[run.sh] Avvio $(date '+%Y-%m-%d %H:%M:%S')"

# --- 1. Clona il repository per leggere/scrivere i file di stato -----------
WORK_DIR=$(mktemp -d)
echo "[run.sh] Clono il repository in $WORK_DIR ..."
git clone --depth=1 \
    "https://x-access-token:${GH_TOKEN}@github.com/${GH_REPO}.git" \
    --branch "${GH_BRANCH:-main}" \
    "$WORK_DIR"

STATE_DIR="$WORK_DIR/state"
mkdir -p "$STATE_DIR"

# --- 2. Esegui il watcher ---------------------------------------------------
echo "[run.sh] Eseguo InterpelliWatcher ..."
export STATE_DIR
export LOG_FILE=off
export NOTIFY_WHEN_NO_NEWS="${NOTIFY_WHEN_NO_NEWS:-false}"

dotnet /app/InterpelliWatcher.dll
EXIT_CODE=$?

echo "[run.sh] InterpelliWatcher terminato con codice $EXIT_CODE"

# --- 3. Committa i file di stato aggiornati --------------------------------
cd "$WORK_DIR"

if [ -z "$(git status --porcelain state)" ]; then
    echo "[run.sh] Nessuna modifica ai file di stato, niente da committare."
else
    echo "[run.sh] Aggiorno i file di stato nel repository..."
    git config user.name  "railway-bot"
    git config user.email "railway-bot@noreply"
    git add state
    git commit -m "Aggiorna stato interpelli [skip ci]"

    # Retry in caso di push concorrenti (improbabile con Railway, ma meglio gestirlo)
    for tentativo in 1 2 3; do
        git pull --rebase origin "${GH_BRANCH:-main}" && \
        git push origin "HEAD:${GH_BRANCH:-main}" && \
        echo "[run.sh] Push completato." && break
        echo "[run.sh] Push fallito, ritento ($tentativo/3)..."
        sleep 5
    done
fi

# --- 4. Pulizia ------------------------------------------------------------
rm -rf "$WORK_DIR"

exit $EXIT_CODE
