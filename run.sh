#!/usr/bin/env bash
#
# One-shot demo runner for the RabbitMQ Streams playground.
# Works with either Docker or Podman (auto-detected).
#
#   ./run.sh          -> start broker + both apps, send a sample update, tail logs
#   ./run.sh stop     -> stop the apps and the broker
#
# Force a specific engine with:  CONTAINER_ENGINE=podman ./run.sh
#
# Ctrl+C stops the two .NET apps (the broker keeps running so you can keep playing;
# use './run.sh stop' to tear the broker down too).
set -euo pipefail
cd "$(dirname "$0")"

API=http://localhost:5080
UI=http://localhost:15672

# --- Container engine detection ---------------------------------------------
# Prefer Docker if present, else Podman. Override with CONTAINER_ENGINE.
ENGINE="${CONTAINER_ENGINE:-}"
if [[ -z "$ENGINE" ]]; then
  if command -v docker >/dev/null 2>&1; then ENGINE=docker
  elif command -v podman >/dev/null 2>&1; then ENGINE=podman
  else echo "ERROR: neither 'docker' nor 'podman' found on PATH." >&2; exit 1; fi
fi

# Resolve the compose command for the chosen engine. Both 'docker compose' and
# 'podman compose' are plugin subcommands; some Podman installs only ship the
# standalone 'podman-compose'. Pick whatever exists.
compose() {
  if [[ "$ENGINE" == "podman" ]]; then
    if podman compose version >/dev/null 2>&1; then podman compose "$@"
    elif command -v podman-compose >/dev/null 2>&1; then podman-compose "$@"
    else echo "ERROR: need 'podman compose' or 'podman-compose'." >&2; exit 1; fi
  else
    docker compose "$@"
  fi
}

echo "==> Using container engine: $ENGINE"

# --- stop mode --------------------------------------------------------------
if [[ "${1:-}" == "stop" ]]; then
  echo "Stopping apps..."
  pkill -INT -f "NotificationService" 2>/dev/null || true
  pkill -INT -f "CustomerService" 2>/dev/null || true
  echo "Stopping broker..."
  (cd docker && compose down)
  echo "Done."
  exit 0
fi

# --- cleanup on Ctrl+C ------------------------------------------------------
cleanup() {
  echo; echo "Stopping apps (broker stays up; run './run.sh stop' to remove it)..."
  pkill -INT -f "NotificationService" 2>/dev/null || true
  pkill -INT -f "CustomerService" 2>/dev/null || true
}
trap cleanup INT TERM

# 1. Broker -------------------------------------------------------------------
echo "==> Starting RabbitMQ (streams)..."
(cd docker && compose up -d)

# Engine-agnostic readiness: ask the broker itself instead of relying on the
# engine's health JSON (Docker and Podman expose it under different paths).
echo -n "==> Waiting for broker"
for _ in $(seq 1 60); do
  if $ENGINE exec rabbitmq-streams rabbitmq-diagnostics -q check_running >/dev/null 2>&1 \
     && $ENGINE exec rabbitmq-streams rabbitmq-diagnostics -q check_port_connectivity >/dev/null 2>&1; then
    echo " - ready"; break
  fi
  echo -n "."; sleep 2
done

# 2. Consumer (declares the stream + waits) ----------------------------------
echo "==> Starting Notification Service (consumer)..."
dotnet run --project notification-service/NotificationService > /tmp/notif.log 2>&1 &

# 3. Publisher API ------------------------------------------------------------
echo "==> Starting Customer Service (publisher API)..."
dotnet run --project customer-service/CustomerService > /tmp/cust.log 2>&1 &

echo -n "==> Waiting for the API"
for _ in $(seq 1 40); do
  curl -fs -o /dev/null "$API/customers" 2>/dev/null && { echo " - ready"; break; }
  echo -n "."; sleep 2
done

# 4. Sample update ------------------------------------------------------------
echo "==> Sending a sample email change for C-001..."
curl -s -X PUT "$API/customers/C-001" \
  -H 'content-type: application/json' \
  -d '{"fullName":"Ada Lovelace","email":"ada.changed@example.com","phoneNumber":"+1-555-0100"}'
echo

cat <<EOF

------------------------------------------------------------------
Everything is running (engine: $ENGINE).

  Publisher API : $API   (PUT /customers/{id} to trigger events)
  Management UI : $UI   (user: app  /  pass: app-pass)
                  -> "Streams" tab -> customer.events

Try more updates, e.g.:
  curl -X PUT $API/customers/C-002 -H 'content-type: application/json' \\
    -d '{"fullName":"Alan Turing","email":"alan@example.com","phoneNumber":"+1-555-9999"}'

Tailing the CONSUMER log below (Ctrl+C to stop the apps)...
------------------------------------------------------------------

EOF

tail -f /tmp/notif.log
