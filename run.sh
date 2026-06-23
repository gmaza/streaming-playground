#!/usr/bin/env bash
#
# One-shot demo runner for the RabbitMQ Streams playground.
#   ./run.sh          -> start broker + both apps, send a sample update, tail logs
#   ./run.sh stop     -> stop the apps and the broker
#
# Ctrl+C stops the two .NET apps (the broker keeps running so you can keep playing;
# use './run.sh stop' to tear the broker down too).
set -euo pipefail
cd "$(dirname "$0")"

API=http://localhost:5080
UI=http://localhost:15672

# --- stop mode --------------------------------------------------------------
if [[ "${1:-}" == "stop" ]]; then
  echo "Stopping apps..."
  pkill -INT -f "NotificationService" 2>/dev/null || true
  pkill -INT -f "CustomerService" 2>/dev/null || true
  echo "Stopping broker..."
  (cd docker && docker compose down)
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
(cd docker && docker compose up -d)

echo -n "==> Waiting for broker to be healthy"
for _ in $(seq 1 40); do
  hs=$(docker inspect -f '{{.State.Health.Status}}' rabbitmq-streams 2>/dev/null || echo absent)
  [[ "$hs" == "healthy" ]] && { echo " - healthy"; break; }
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
Everything is running.

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
