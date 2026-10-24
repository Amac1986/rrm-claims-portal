# Azure deployment: infrastructure as code

This directory contains the Bicep for the optional Azure deployment of the Claims Portal.

**Status: authored, offline-validated and `what-if`-verified against a real subscription.
Not deployed. Nothing billable has been created in Azure.** The deployment is cost-gated:
the exact shape is priced below, and no billable resource is created until the repository
owner confirms the number.

**Cost shape approved on 2026-10-24: the owner accepted the pay-as-you-go shape at about
$31/month and instructed that the create be held until FIR-9 (the Service Bus transport
swap) lands.** FIR-9's transport swap is now implemented in both apps, so the crash-loop
blocker is gone. The approval authorises this exact shape only; it is not an instruction to
create resources, so `az deployment group create` stays un-run and the resource group stays
empty until the owner gives the go-ahead. `what-if` should be re-run to confirm there is no
drift before the create, bearing in mind the Service Bus transport has not yet been observed
against a live namespace.

## What the template creates

| Resource | Shape | Why |
| --- | --- | --- |
| Container Apps environment | Consumption plan, public ingress | Hosts the four container apps. No Dedicated workload profile, no VNet, no private endpoint. |
| `api` container app | internal HTTP ingress on 8080, `minReplicas: 0` | The web app proxies `/api/` to it; it is not exposed directly. |
| `worker` container app | no ingress, `minReplicas: 0`, KEDA Service Bus queue scaler | Wakes on `claim-submitted-notifications` queue depth, costs nothing at rest. |
| `web` container app | external HTTPS ingress on 8080, `minReplicas: 0` | nginx serves the React build and proxies `/api/` to the internal API app. |
| `mailpit` container app | external HTTP on 8025, internal TCP 1025, `minReplicas: 0` | Catches the confirmation email. The SMTP port is an extra internal TCP mapping, not public. |
| PostgreSQL Flexible Server | Burstable `Standard_B1ms`, 1 vCore / 2 GiB, 32 GiB storage, 7-day backup, no HA | The free-account-eligible shape. |
| Service Bus | Standard namespace, `claim-submitted-notifications` queue, namespace-scoped SAS rule | Standard is required for MassTransit topics. The queue is pre-created so KEDA has something to measure. |
| Static Web Apps | Free tier | Defined for the React app, but not wired to a deployment in this slice; the Container Apps `web` app is the working front door. |
| Log Analytics workspace | `PerGB2018`, 30-day retention, 0.1 GB/day cap | Container Apps environment logs. The cap keeps usage inside the 5 GB/month free allowance. |

Secrets are Container Apps secrets referenced by name from environment variables. The names
are the contract with the deployment pipeline:

| Secret name | Used by | Contents |
| --- | --- | --- |
| `postgres-connection-string` | API, worker | Full Npgsql connection string, including the admin password. |
| `servicebus-connection-string` | API, worker, Mailpit (KEDA) | Service Bus SAS connection string. |
| `ghcr-token` | All apps, only when `usePrivateRegistry` is true | GHCR token for private images. |

No secret value is committed. `main.bicepparam` reads `POSTGRES_ADMIN_PASSWORD`, `GHCR_USER`
and `GHCR_TOKEN` from the environment.

## Cost

Prices are from the Azure Retail Prices API for `canadacentral`, in USD, queried on
2026-10-23. They exclude tax and any subscription-level credit.

| Resource | Meter | Rate | Monthly at rest / demo volume |
| --- | --- | --- | --- |
| Container Apps Consumption | active vCPU | $0.000034/second | $0 within the free grant |
| Container Apps Consumption | active memory | $0.000004/GiB-second | $0 within the free grant |
| Container Apps Consumption | requests | $0.40/million | $0 within the free grant |
| PostgreSQL Flexible Server | `B1MS` compute | $0.0185/hour | $13.51 at 730 hours |
| PostgreSQL Flexible Server | storage | $0.1265/GB-month | $4.05 for 32 GiB |
| PostgreSQL Flexible Server | backup storage | $0.105/GB-month | up to $3.36 for 32 GiB |
| Service Bus | Standard base unit | $10.00/month | $10.00 |
| Static Web Apps | Free | $0 | $0 |
| Log Analytics | analytics logs ingestion | 5 GB/month free, then $2.76/GB | $0 with the 0.1 GB/day cap |
| Bandwidth | egress | first 100 GB/month free | $0 |

The Container Apps free grant is 180,000 vCPU-seconds, 360,000 GiB-seconds and 2 million
requests per subscription per month. All four apps are configured with `minReplicas: 0`, so
there is no idle compute charge. For scale: one always-on 0.25 vCPU / 0.5 GiB replica
consumes about 657,000 vCPU-seconds and 1.315 million GiB-seconds per month, which exceeds
the vCPU grant by itself. At the current rates, one always-on replica would cost roughly
$20/month after the grant; four would cost roughly $80/month. That is why scale-to-zero and
the KEDA queue scaler are requirements, not tuning.

**Expected total**

- With the Azure free-account PostgreSQL offer applying: about **$10/month**, almost all of
  it the Service Bus Standard base unit. The initial free-account credit may cover this.
- On a pay-as-you-go subscription, where the PostgreSQL free offer does not apply: about
  **$31/month** (PostgreSQL compute, storage and backup add about $21/month).

The free-account PostgreSQL offer must be verified after provisioning. It is not a property
of this template and it does not apply to every subscription. Check Cost Management for the
resource group, or confirm the offer on the subscription, before assuming the lower number.

**The authenticated subscription is pay-as-you-go.** Its
`subscriptionPolicies.quotaId` is `PayAsYouGo_2014-09-01` with `spendingLimit: Off`, not a
free-account quota. The 12-month PostgreSQL offer is a free-account entitlement and, per the
task, does not apply to pay-as-you-go. There is no subscription metadata here that proves the
offer is active, so **budget the ~$31/month column**; treat ~$10/month as upside only if Cost
Management later shows the PostgreSQL meters at zero.

## Self-hosting PostgreSQL: not cheaper on Container Apps

The managed PostgreSQL line item is the part people ask about first: can we run Postgres
ourselves and avoid it? Technically yes, but on Container Apps it costs more than the
managed server and gives up backups.

A database cannot use `minReplicas: 0`. The API and worker need it running on the first
request, and a stopped database loses its process state, so at least one replica is always
on. That is exactly the usage the free grant does not cover: at 730 hours, one 0.25 vCPU /
0.5 GiB replica consumes 657,000 vCPU-seconds and 1,314,000 GiB-seconds, against the monthly
grant of 180,000 vCPU-seconds and 360,000 GiB-seconds.

| Option | Compute/month | Storage/month | Backups | Total/month |
| --- | --- | --- | --- | --- |
| Postgres container, 0.25 vCPU / 0.5 GiB, SMB share | ~$20.03 | ~$2.11 for 32 GiB | none | not viable: SMB is not a supported data directory |
| Postgres container, 0.5 vCPU / 1 GiB, NFS premium share | ~$47.63 | $17.60 (100 GiB minimum) | none | ~$65.23 |
| PostgreSQL Flexible Server B1ms | $13.51 | $4.05 storage + up to $3.36 backup | 7 days, managed | $20.91, or **$0 for 12 months** on the free account |

Compute uses the Retail Prices API **active** rates for `canadacentral` (vCPU
$0.000034/second, memory $0.000004/GiB-second) after the monthly free grant. A running
replica that stays below the activity threshold is billed at the lower idle rate, so
0.25 vCPU / 0.5 GiB costs roughly $5.73/month idle and $20.03/month active; treat the table
as the upper bound, not a fixed price. A database doing background work and serving
connections is not reliably idle. Storage uses the premium Azure Files rate
($0.176/GiB-month) because the durable NFS mount is SSD-only, with a 100 GiB minimum
provisioned share.

The storage mount is the catch. Container Apps has no persistent local disk; durable data
must live on Azure Files. The SMB share is not a supported PostgreSQL data directory, and
the NFS share requires a VNet-integrated Container Apps environment and a storage account
restricted to that VNet. That replaces this template's simple public environment with a
private network, and it still has no managed backup or patching.

Self-hosting on your own machine or VM is the only genuinely free option, but then the
Container Apps API and worker need a reachable database endpoint, with its own network,
TLS and backup story. That is a different deployment, not a parameter on this one.

**Decision: keep the managed B1ms.** It is free for the 12-month window this interview sits
in, it includes backups and patching, and it is cheaper than the smallest viable
self-hosted Container Apps replica. The only recurring line item that survives the free
offer is Service Bus Standard at $10/month. MassTransit publishes to topics, and Azure
Storage Queues are not a transport it supports (see the next section); Service Bus Basic
has no topics and is not a drop-in either. The way to avoid ongoing cost is teardown after
the interview, not a self-hosted database.

If the goal is zero Azure spend rather than zero ongoing cost, use the no-deploy fallback:
run the stack with `docker compose` locally for the demo and hand in this directory as
infrastructure-as-code that was never applied. That is the option this task explicitly
allows, and it avoids both the PostgreSQL and Service Bus charges.

## Storage Queue instead of Service Bus: not a drop-in

The question is fair: the flow is one publisher to one consumer, so a queue would be enough
for the interview. The blocker is not the topology, it is MassTransit. It is pinned to
8.5.12 (the last Apache-2.0 line), and that line has no Azure Storage Queue transport.
`MassTransit.Azure.Storage` is MessageData persistence (large payloads in Blob Storage), not
a transport. Moving the claim-to-notification hop to `Azure.Storage.Queues` therefore means
removing MassTransit from that path on both sides:

- **The API outbox goes with it.** `ClaimPortal.Api/Program.cs` publishes through
  `IPublishEndpoint`, and `UseBusOutbox` is what makes the claim row and the
  `ClaimSubmitted` message commit atomically in the same Postgres transaction. A raw queue
  needs a hand-written outbox dispatcher, or the deployment gives up that guarantee.
- **The consumer pipeline goes with it.** `ClaimPortal.Notifications` gets retry, delayed
  redelivery and the `_error` queue from MassTransit. Storage Queues have no delayed
  delivery, so the 5/15/60-minute `UseDelayedRedelivery` intervals would become hand-rolled
  visibility-timeout deferrals, or be dropped, losing the ~90-minute tolerance for a mail
  relay outage.
- **`Publish` becomes `Send`.** One consumer today, but `Publish` is the seam that lets a
  second consumer be added without touching the API; a queue hard-codes the consumer.
- **KEDA is not the blocker.** Container Apps supports an `azure-queue` scaler, so
  scale-to-zero would still work.

Cost, for completeness (canadacentral, USD, Retail Prices API): Queue Storage is about
$0.004 per 10,000 GPv2 LRS operations plus $0.045/GB-month stored, which is cents per month
at interview volume, against $10/month for Service Bus Standard. The saving is real, but the
change is a transport rewrite in product code, not a parameter in this directory, and it
belongs to the transport task (FIR-9).

**Decision: keep Service Bus Standard for the interview.** If the goal is zero Azure spend,
the no-deploy fallback is the better trade: local `docker compose` on RabbitMQ is free and
needs no rewrite. If the $10 base unit matters after the interview and a rewrite is not
acceptable, Service Bus **Basic** has no base unit ($0.05 per million operations) but no
topics; using it means sending to the notification queue instead of publishing, which is a
smaller product-code change than replacing MassTransit but still couples the API to the
queue name, the coupling the current design deliberately avoids.

## Cost gate and `what-if`

The template has been validated offline and previewed against the subscription:

```text
bicep build deploy/azure/main.bicep   -> exit 0, no diagnostics
bicep lint  deploy/azure/main.bicep   -> exit 0, no diagnostics
bicep build-params deploy/azure/main.bicepparam -> exit 0 (with a dummy password)
az deployment group what-if -g rrm-claims-portal -p deploy/azure/main.bicepparam
                                      -> exit 0, 13 resources to create, no changes/errors
```

The `what-if` preview ran on 2026-10-23 with Azure CLI 2.91.0 against an authenticated
subscription, resource group `rrm-claims-portal` (`canadacentral`, empty apart from its
`purpose=interview-whatif` tag). It reported the four container apps, the managed
environment, PostgreSQL flexible server + database + firewall rule, the Service Bus
namespace + SAS rule + queue, the Log Analytics workspace and the Static Web App as
`+ Create`, with `minReplicas: 0` on all four apps. It is a read-only preview: no resource
was created and no provider was registered.

The resource providers are not registered on the subscription yet
(`Microsoft.App`, `Microsoft.DBforPostgreSQL` and `Microsoft.ServiceBus` all report
`NotRegistered`). Registering them is free and is a one-time pre-deploy step; the first
`az deployment group create` will fail without it:

```bash
for ns in Microsoft.App Microsoft.DBforPostgreSQL Microsoft.ServiceBus Microsoft.OperationalInsights Microsoft.Web; do
  az provider register --namespace "$ns" --wait
done
```

The cost report and the preview output were posted on the task. **The owner accepted the
pay-as-you-go shape at about $31/month on 2026-10-24, with the create explicitly held until
FIR-9 (the Service Bus transport swap) lands.** FIR-9 is now implemented in both apps, so the
crash-loop blocker is gone, but the create is still held: it stays un-run until the owner
gives the go-ahead. When that comes, re-run `what-if` to confirm there is no drift, then:

```bash
export POSTGRES_ADMIN_PASSWORD='<strong-password>'
# Only if the GHCR packages are private:
# export GHCR_USER='<github-user>'
# export GHCR_TOKEN='<token>'

az deployment group create \
  --resource-group rrm-claims-portal \
  --parameters deploy/azure/main.bicepparam
```

The deployment outputs the web and Mailpit URLs, the API internal FQDN, the PostgreSQL FQDN,
the Service Bus namespace/queue and the container app names. The CI deploy stage uses the
`AZURE_RESOURCE_GROUP` and `AZURE_CONTAINER_APP_{API,WORKER,WEB}` variables; set them to the
outputs (`rrmclaims-api`, `rrmclaims-worker`, `rrmclaims-web` with the default prefix).

## Teardown

Delete the whole resource group after the interview. It contains every billable resource
created by this template, including PostgreSQL, Service Bus and the log workspace:

```bash
az group delete --name rrm-claims-portal --yes --no-wait
```

Verify it is gone:

```bash
az group exists --name rrm-claims-portal
```

`false` means the group and its resources are deleted. The Static Web App is free, but it is
inside the same resource group and is removed by the same command.

If the cost shape is rejected or the demo runs locally instead, delete the empty
`rrm-claims-portal` group with the same command. It contains no billable resource.

## Known blockers before a live deployment is useful

1. **The Service Bus transport has never been run against a live namespace.** FIR-9 wired the
   transport into `ClaimPortal.Api/Program.cs` and
   `ClaimPortal.Notifications/NotificationsServiceCollectionExtensions.cs`, and both apps now
   select it from `Messaging__Transport=AzureServiceBus` instead of throwing. But the then-current
   Bicep preview is the only evidence: the topology (topic, subscription, auto-forwarding to the
   queue) and the `_error` versus native dead-letter behaviour are reviewed, not observed. Expect
   to verify both on first deployment; see `docs/TRANSPORT.md`.
2. **Topic naming agrees by construction, but confirm it on the first run.** The worker's
   `SetKebabCaseEndpointNameFormatter()` names the receive endpoint only; it does not change the
   message entity name. Both apps use the default entity name formatter, so the API publishes to
   the `ClaimSubmitted` topic and the worker subscribes to it. The queue name
   (`claim-submitted-notifications`) is fixed in configuration, pre-created by Bicep, and used
   directly by the consumer definition.
3. **Static Web Apps is provisioned but not wired.** The Vite build has no API base URL baked
   in; it relies on the nginx proxy in the `web` container. Deploying the same bundle to
   Static Web Apps would need either a linked API backend or an absolute
   `VITE_API_BASE_URL` plus a CORS policy. The `web` container app is the working front door
   for this deployment. The Free SWA resource costs nothing but is not evidence of a second
   working front end.
4. **Mailpit is best-effort and public.** It scales to zero and wakes on the same Service Bus
   queue as the worker. There is a startup race: the worker can publish an email before
   Mailpit is listening, but the worker's retry/redelivery policy tolerates it. The Mailpit
   UI is exposed publicly and has no authentication; it is a disposable demo catcher, not a
   production mail service.
5. **PostgreSQL is publicly reachable by Azure services.** The firewall rule is the standard
   `0.0.0.0` "Allow Azure services" rule, because the Container Apps environment is not
   VNet-integrated. That is acceptable for this disposable demo, but it is not the production
   shape. A VNet-integrated environment and private access would be the next step and would
   also add cost.
