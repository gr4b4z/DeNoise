# ADR-18: The Data Protection key ring lives on a shared ReadWriteMany volume, not in the database

**Status:** Accepted. Implements the "data-protection key" half of [ADR-11](../03-architecture-and-adrs.md) for Helm deployments.

## Context

ASP.NET Core Data Protection protects two kinds of data in DeNoise:

- **Per-row secrets** (`DataProtectionSecretProtector`, purpose `DeNoise.RowSecrets.v1`): destination URLs, headers and
  signing secrets, integration HMAC secrets and the Atlas state-query API key. They are written by the **Api** and read by
  the **Ingest** host (signature verification) and the **Workers** (dispatcher, Atlas state query).
- **CSRF tokens** (`CsrfTokens`, purpose `DeNoise.Csrf.v1`): issued by one Api replica and verified by whichever replica
  receives the next mutation.

All hosts already share the application name `DeNoise`, so they can read one another's payloads only if they also share
the key ring. The application persists the ring to `DataProtection:KeysPath` when that is set
(`PersistKeysToFileSystem`) and keeps it in memory otherwise. The Helm chart set neither the path nor a volume, and the
containers run with `readOnlyRootFilesystem`. Each pod therefore had its own in-memory ring: after a restart every
encrypted row became unreadable, Ingest could not verify any HMAC secret written by the Api, and a second Api replica
rejected the first one's CSRF tokens. The release repository pinned `api.replicas` to 1 because of this.

Two ways to share the ring were considered.

1. **A ReadWriteMany volume** (Azure Files through the built-in `azurefile-csi` storage class on AKS) mounted at
   `DataProtection:KeysPath` in Api, Ingest and Workers. No application code changes; the chart gains a PVC and a mount.
2. **The database** (`PersistKeysToDbContext`): `DeNoiseDbContext` implements `IDataProtectionKeyContext`, a new
   `DataProtectionKeys` table arrives with a migration, and every host already has the connection string.

## Decision

Option 1. The chart gets `dataProtection.keysPath` (default `/var/denoise/keys`), `dataProtection.persistentVolumeClaim`
(an existing RWX claim) and `dataProtection.createPvc` with `storageClassName` (default `azurefile-csi`) and `size`
(default `1Gi`). When either PVC value is set, the claim is mounted at `keysPath` in the three hosts and
`DataProtection__KeysPath` points there. The root filesystem stays read-only, since the mount is the only writable path
besides `/tmp`. A chart-created PVC carries `helm.sh/resource-policy: keep`, so `helm uninstall` never deletes the keys.
Deleting the keys would make every stored secret unreadable.

Without a PVC the chart mounts a per-pod `emptyDir` at the same path. Keys then survive a container restart but not a
rescheduled pod. That is the old ephemeral behaviour made explicit, fit only for single-replica development such as a
kind cluster. `NOTES.txt` warns about it.

The migrator Job gets neither the variable nor the mount. It is a pre-install hook, so on a first install it runs
before a chart-created PVC exists. It also protects nothing during a plain migration.

### Why not the database

The database would be simpler, with no storage class, no PVC and nothing extra to back up. It would not be safer, and
being safer is the reason the ring is shared at all.

- **Keys next to the data they protect.** Without a key-encryption key (`ProtectKeysWith…`, not configured today) the
  ring is stored as plain XML. In a table it would sit in the same database, the same `pg_dump`, the same backup
  storage and behind the same connection string as the ciphertext it decrypts. Read access to the database would then
  expose every destination secret. ADR-11 encrypts those rows so that a database leak alone does not do that. On a
  volume the two are separate: the share needs separate Azure Storage access, and it is never in a database backup.
- **More moving parts in the application.** A new package (`Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`), a
  forward-only migration, and a key-ring read that depends on database availability during startup. Today a host starts
  and reports readiness even while PostgreSQL is down.

The database becomes acceptable once the ring itself is wrapped by a Key Vault key, because a dump would then hold only
wrapped keys. That step is the follow-up below in any case, and moving the ring is a one-off copy of the XML files into
the table.

## Consequences

- `api.replicas` can go above 1 once a RWX claim is configured. Ingest and Workers read secrets written by the Api.
  Encrypted rows survive restarts, rollouts and node drains.
- **Backups must include the share.** A database restored without the matching key ring decrypts nothing. The restore
  drill (`build/backup-restore-drill.sh`) and the hub-failure runbook have to cover Azure Files snapshots or backup next
  to the PostgreSQL point-in-time restore. The restored ring must be at least as new as the database backup. A newer
  ring is fine, because Data Protection keeps expired keys for decryption.
- Several pods starting against an empty share may each create a key. All of them are valid and persisted. A payload
  under a key an instance has not loaded yet fails until that instance reloads the ring, which it does on such a miss
  shortly after startup and periodically after that. The worst case is one rejected CSRF token on the very first rollout.
- `azurefile-csi` mounts with mode `0777`, so the non-root `denoise` user can write without an `fsGroup`. A different
  RWX class may need its own mount options.
- The keys are still stored unencrypted on the share. Data Protection logs *No XML encryptor configured* at startup.
  **Follow-up:** wrap the ring with a Key Vault key (`ProtectKeysWithAzureKeyVault`, package
  `Azure.Extensions.AspNetCore.DataProtection.Keys`, workload identity for the three hosts). That completes ADR-11's
  "data-protection key from Key Vault" and would make the database an equal option.
- `dotnet run` and the tests are unchanged. Without `DataProtection:KeysPath` the ring stays in memory.
