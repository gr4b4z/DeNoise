# ADR-18: The Data Protection key ring is generated once and supplied from Key Vault

**Status:** Accepted, revised 2026-10-22. Implements the "data-protection key from Key Vault" half of
[ADR-11](../03-architecture-and-adrs.md). The first version of this ADR chose a shared ReadWriteMany volume. That
remains an option in the Helm chart but is no longer the recommended set-up (see *Revision*).

## Context

ASP.NET Core Data Protection protects two kinds of data in DeNoise:

- **Per-row secrets** (`DataProtectionSecretProtector`, purpose `DeNoise.RowSecrets.v1`): destination URLs, headers and
  signing secrets, integration HMAC secrets and the Atlas state-query API key. They are written by the **Api** and read
  by the **Ingest** host (signature verification) and the **Workers** (dispatcher, Atlas state query).
- **CSRF tokens** (`CsrfTokens`, purpose `DeNoise.Csrf.v1`): issued by one Api replica and verified by whichever replica
  receives the next mutation.

All hosts use the application name `DeNoise`, so they can read one another's payloads only if they also share the key
ring. Without configuration each process keeps its own ring in memory. Encrypted rows are then unreadable after a
restart, Ingest cannot verify HMAC secrets written by the Api, and a second Api replica rejects the first one's CSRF
tokens. The hosts run as three separate deployables (Helm Deployments on AKS, or three container apps on Azure
Container Apps), so any platform feature that shares keys among the replicas of *one* app is not enough.

Options considered:

1. **A shared volume** (Azure Files ReadWriteMany on AKS) mounted at `DataProtection:KeysPath`. The hosts create and
   rotate keys themselves. Needs a storage class, a PVC, a mount in every host, and a backup of the share.
2. **The database** (`PersistKeysToDbContext`). Needs a new package and a migration. Without a key-encryption key the
   ring is plain XML in the same database, the same dumps and the same backups as the ciphertext it protects.
3. **Blob Storage with a Key Vault key** (`PersistKeysToAzureBlobStorage` + `ProtectKeysWithAzureKeyVault`), Microsoft's
   reference set-up for Azure. Needs a storage account, two packages, and a managed or workload identity in every host.
4. **Azure Container Apps' built-in key management** (`autoConfigureDataProtection`). Shares keys among the replicas and
   revisions of one container app. It does not cover three apps sharing one ring, does not exist on AKS, and gives way
   as soon as the app configures Data Protection itself.
5. **A key generated once and stored in Key Vault**, handed to every host as configuration. The hosts never write keys.

## Decision

Option 5.

- `DeNoise.Migrator generate-dataprotection-key [--lifetime-days N] [--activate-in-days N] [--out FILE]` creates one
  key with the Data Protection key manager itself and writes it as XML. The default lifetime is 3650 days. The command
  needs no database and prints status lines to stderr only.
- The document is stored as a Key Vault secret and reaches every host as `DataProtection__Keys__0` (and `__1`, … during
  a rotation). In Helm this is `dataProtection.keysSecret` / `keysSecretKeys` (`secretKeyRef`). In Azure Container Apps
  it is a secret with a Key Vault reference, bound to the same environment variable.
- `DataProtection:Keys` makes the ring **read-only**: a repository over the configured documents, with automatic key
  generation disabled. An invalid document fails host start-up with its index, never its content. Configuring both
  `Keys` and `KeysPath` fails start-up too.
- `DataProtection:ReadOnly=true` gives the same read-only behaviour to a `KeysPath` directory, for example a mounted
  Secret.
- `KeysPath` with a writable shared volume (option 1) stays supported. Without any of these the ring is in memory, as
  before.

### Why not the others

- **Volume:** works, but brings storage infrastructure to every environment. The keys sit unencrypted on the share,
  and the share is a second thing to back up in step with the database.
- **Database:** simplest to operate, but key and ciphertext end up in one place. Anyone who can read the database can
  read every destination secret, which ADR-11 encrypts precisely so that a database leak alone does not do that.
- **Blob Storage + Key Vault key:** the most complete set-up, with automatic rotation and wrapped keys, but the most
  infrastructure. It becomes worth it if manual rotation turns out to be a burden; the configured keys can then be
  imported into the blob.
- **Container Apps auto-configuration:** does not share one ring across three apps.

With option 5 the key exists only in Key Vault and in the memory of the running hosts. It never lands in a database
backup or on a file share, and the release process already syncs Key Vault secrets into the hosts.

## Consequences

- **Bootstrap step:** after Key Vault exists, an operator with *Key Vault Secrets Officer* runs the command once and
  stores the output:
  ```
  dotnet run --project src/DeNoise.Migrator -- generate-dataprotection-key --out key.xml
  az keyvault secret set --vault-name <kv> --name denoise-dataprotection-key-1 --file key.xml
  ```
  Then the local file is deleted. Deploying before the secret exists must fail rather than start hosts without keys.
- **No automatic rotation.** The ring never creates a successor. Once the newest key expires the hosts can still
  decrypt, but every *protect* fails (pinned by a unit test). Rotation, well before expiry:
  1. Generate a new key with `--activate-in-days 2`, so that every host has loaded it before it becomes the default.
  2. Store it as `denoise-dataprotection-key-2` and add it as `DataProtection__Keys__1`.
  3. Deploy, keeping the old key.

  Old keys stay configured for as long as rows protected with them may exist. Removing one makes those rows
  unreadable.
- **Losing the Key Vault secret loses every encrypted row.** Key Vault soft delete and purge protection cover
  accidental deletion.
- `api.replicas` can go above 1. All hosts read one another's secrets, restarts and rollouts keep encrypted rows readable.
- The master key is unencrypted inside the secret. That is acceptable because Key Vault is the protection: access is
  RBAC-scoped and audited, and the document never leaves Key Vault except into the hosts' environment.
- `dotnet run` and the tests are unchanged: without configuration the ring stays in memory.

## Revision

The first version (same day) chose option 1 and added the chart's `dataProtection.persistentVolumeClaim` /
`createPvc`. Mapping the release to the target platform showed a volume to be the heavier option. Azure Container
Apps, the intended target, would need an Azure Files storage mount in each of the three apps. On AKS it brings a
storage class and a second backup. Option 5 needs neither. The PVC values remain in the chart for clusters that prefer
a writable shared ring.
