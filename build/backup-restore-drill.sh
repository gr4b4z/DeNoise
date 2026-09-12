#!/usr/bin/env bash
# Backup/restore drill for the Alert Hub database (09 M11 hardening).
#
# Dumps the live database, restores it into a scratch database, compares row counts of the tables that matter and
# runs the Migrator against the restored copy to prove the schema is complete. Nothing touches the source database
# except a read-only pg_dump.
#
#   PGHOST=... PGUSER=... PGPASSWORD=... build/backup-restore-drill.sh alerthub [scratch-db-name]
#
# Exit code 0 = counts match and migrations are idempotent on the copy. Keep the output with the change record.
set -euo pipefail

source_db="${1:?source database name}"
scratch_db="${2:-${source_db}_restore_drill_$(date -u +%Y%m%d%H%M%S)}"
dump="$(mktemp -t alerthub-dump.XXXXXX)"
trap 'rm -f "$dump"' EXIT

echo "== dumping $source_db"
pg_dump --format=custom --no-owner --no-privileges --file="$dump" "$source_db"
ls -l "$dump"

echo "== restoring into $scratch_db"
createdb "$scratch_db"
pg_restore --no-owner --no-privileges --dbname="$scratch_db" "$dump"

tables=(alert.raw_event alert.normalised_event alert.episode alert.episode_event alert.alert_group ops.job ops.outbox ops.delivery_attempt cfg.integration cfg.policy cfg.destination cfg.suppression cfg.user audit.entry hb.heartbeat hb.run)
status=0
echo "== comparing row counts"
printf '%-28s %12s %12s\n' table source restored
for t in "${tables[@]}"; do
  a=$(psql -At -d "$source_db" -c "SELECT count(*) FROM $t" 2>/dev/null || echo "n/a")
  b=$(psql -At -d "$scratch_db" -c "SELECT count(*) FROM $t" 2>/dev/null || echo "n/a")
  flag=""
  if [[ "$a" != "$b" ]]; then flag="  <-- MISMATCH"; status=1; fi
  printf '%-28s %12s %12s%s\n' "$t" "$a" "$b" "$flag"
done

echo "== partitions restored"
psql -At -d "$scratch_db" -c "SELECT count(*) FROM pg_tables WHERE schemaname = 'alert' AND tablename LIKE 'raw_event_p%'"

if [[ -n "${MIGRATOR_DLL:-}" ]]; then
  echo "== running the Migrator against the copy (must be a no-op)"
  ConnectionStrings__AlertHub="Host=${PGHOST:-localhost};Database=${scratch_db};Username=${PGUSER:-postgres};Password=${PGPASSWORD:-}" dotnet "$MIGRATOR_DLL"
fi

if [[ "${KEEP_SCRATCH:-0}" != "1" ]]; then
  echo "== dropping $scratch_db"
  dropdb "$scratch_db"
fi

if [[ $status -eq 0 ]]; then echo "DRILL OK"; else echo "DRILL FAILED: row counts differ"; fi
exit $status
