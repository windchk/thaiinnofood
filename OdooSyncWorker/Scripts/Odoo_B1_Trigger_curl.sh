#!/usr/bin/env bash
set -euo pipefail

# SAP -> Odoo B1 trigger request checker
#
# Current TEST result observed on 2026-07-22:
#   POST /api/v1/b1/production -> HTTP 404
#   POST /api/v1/b1/delivery   -> HTTP 404
#
# This script sends a real POST request. Use TEST first and run one case at a time.
# It intentionally does not contain the real API key.

BASE_URL="${BASE_URL:-http://192.168.10.2:8069}"
API_KEY_HEADER="${API_KEY_HEADER:-x-api-key}"
API_KEY="${API_KEY:-}"
SITE_ID="${SITE_ID:-TEST}"
SAP_DATABASE_NAME="${SAP_DATABASE_NAME:-TEST_INTERFACE}"
DOC_ENTRY="${DOC_ENTRY:-47805}"
CASE_NAME="${1:-}"

show_usage() {
  cat <<'USAGE'
Usage:
  API_KEY='<key>' bash ./Odoo_B1_Trigger_curl.sh production-release
  API_KEY='<key>' bash ./Odoo_B1_Trigger_curl.sh production-cancel
  API_KEY='<key>' bash ./Odoo_B1_Trigger_curl.sh delivery-release
  API_KEY='<key>' bash ./Odoo_B1_Trigger_curl.sh delivery-cancel

Optional environment variables:
  BASE_URL       Default: http://192.168.10.2:8069
  API_KEY_HEADER Default: x-api-key
  SITE_ID        Default: TEST
  SAP_DATABASE_NAME
                  Value sent as companyName. Default: TEST_INTERFACE
  DOC_ENTRY      Default: 47805

Examples:
  API_KEY='<key>' SITE_ID=TEST SAP_DATABASE_NAME=TEST_INTERFACE DOC_ENTRY=47805 \
    bash ./Odoo_B1_Trigger_curl.sh production-release

  API_KEY='<key>' SITE_ID=TEST SAP_DATABASE_NAME=TEST_INTERFACE DOC_ENTRY=47805 \
    bash ./Odoo_B1_Trigger_curl.sh delivery-release

  API_KEY='<key>' SITE_ID=TEST SAP_DATABASE_NAME=TEST_STL_ODOO DOC_ENTRY=47805 \
    bash ./Odoo_B1_Trigger_curl.sh delivery-release

Warning:
  production-cancel and delivery-cancel can change the Odoo document state.
USAGE
}

if [[ -z "$CASE_NAME" ]]; then
  show_usage
  exit 2
fi

if [[ -z "$API_KEY" ]]; then
  echo "ERROR: Set API_KEY before running the request." >&2
  exit 2
fi

if [[ -z "$SAP_DATABASE_NAME" ]]; then
  echo "ERROR: SAP_DATABASE_NAME is required." >&2
  exit 2
fi

if [[ ! "$DOC_ENTRY" =~ ^[1-9][0-9]*$ ]]; then
  echo "ERROR: DOC_ENTRY must be a positive integer." >&2
  exit 2
fi

case "$CASE_NAME" in
  production-release)
    endpoint="/api/v1/b1/production"
    doc_state="release"
    collection_name="productions"
    ;;
  production-cancel)
    endpoint="/api/v1/b1/production"
    doc_state="cancel"
    collection_name="productions"
    ;;
  delivery-release)
    endpoint="/api/v1/b1/delivery"
    doc_state="release"
    collection_name="deliverys"
    ;;
  delivery-cancel)
    endpoint="/api/v1/b1/delivery"
    doc_state="cancel"
    collection_name="deliverys"
    ;;
  *)
    echo "ERROR: Unknown case '$CASE_NAME'." >&2
    show_usage
    exit 2
    ;;
esac

printf -v payload \
  '{"params":{"%s":[{"siteId":"%s","docEntry":%s,"docState":"%s","companyName":"%s"}]}}' \
  "$collection_name" \
  "$SITE_ID" \
  "$DOC_ENTRY" \
  "$doc_state" \
  "$SAP_DATABASE_NAME"

request_url="${BASE_URL%/}${endpoint}"

echo "POST $request_url"
echo "Payload: $payload"

curl \
  --silent \
  --show-error \
  --connect-timeout 10 \
  --max-time 30 \
  --request POST \
  --header "Content-Type: application/json" \
  --header "Accept: application/json" \
  --header "${API_KEY_HEADER}: ${API_KEY}" \
  --data-raw "$payload" \
  --write-out $'\nHTTP_STATUS:%{http_code}\n' \
  "$request_url"
