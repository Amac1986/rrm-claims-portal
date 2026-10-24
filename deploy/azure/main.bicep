targetScope = 'resourceGroup'

@description('Azure region for all resources. The resource group region is used by default.')
param location string = resourceGroup().location

@description('Short lowercase prefix used in globally unique resource names.')
@minLength(3)
@maxLength(20)
param namePrefix string = 'rrmclaims'

@description('Tags applied to every resource.')
param tags object = {
  project: 'rrm-claims-portal'
  managedBy: 'bicep'
}

@description('PostgreSQL administrator login.')
@minLength(1)
param postgresAdminLogin string = 'claimportal'

@description('PostgreSQL administrator password. Supply from a pipeline secret, never commit it.')
@secure()
param postgresAdminPassword string

@description('PostgreSQL major version. This matches the local postgres:17-alpine image.')
param postgresVersion string = '17'

@description('PostgreSQL compute SKU. B1ms is the free-account eligible shape.')
param postgresSkuName string = 'Standard_B1ms'

@description('PostgreSQL storage in GiB. 32 GiB is the free-account allowance.')
@minValue(32)
@maxValue(16384)
param postgresStorageGb int = 32

@description('API image, including tag.')
param apiImage string = 'ghcr.io/amac1986/rrm-claims-portal/api:latest'

@description('Worker image, including tag.')
param workerImage string = 'ghcr.io/amac1986/rrm-claims-portal/worker:latest'

@description('Web image, including tag.')
param webImage string = 'ghcr.io/amac1986/rrm-claims-portal/web:latest'

@description('Mailpit image, including tag.')
param mailpitImage string = 'axllent/mailpit:v1.27'

@description('Service Bus queue consumed by the notification worker and used by KEDA.')
param serviceBusQueueName string = 'claim-submitted-notifications'

@description('Set when the GHCR packages are private and require registry credentials.')
param usePrivateRegistry bool = false

@description('Container registry server for private images.')
param containerRegistryServer string = 'ghcr.io'

@description('Container registry username. Supply from a pipeline secret.')
param containerRegistryUsername string = ''

@description('Container registry password or token. Supply from a pipeline secret.')
@secure()
param containerRegistryPassword string = ''

@description('Honeycomb API key. Leave empty for a deployment without tracing; supply from a pipeline secret, never commit it.')
@secure()
param honeycombApiKey string = ''

@description('Create the free-tier Static Web App resource for the React app.')
param deployStaticWebApp bool = true

var suffix = uniqueString(resourceGroup().id)
var logAnalyticsName = toLower('${namePrefix}-logs-${suffix}')
var serviceBusName = toLower('${namePrefix}-sb-${suffix}')
var postgresName = toLower('${namePrefix}-pg-${suffix}')
var environmentName = toLower('${namePrefix}-env-${suffix}')
var apiName = toLower('${namePrefix}-api')
var workerName = toLower('${namePrefix}-worker')
var webName = toLower('${namePrefix}-web')
var mailpitName = toLower('${namePrefix}-mailpit')
var staticWebAppName = toLower('${namePrefix}-swa-${suffix}')

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    workspaceCapping: {
      dailyQuotaGb: json('0.1')
    }
  }
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: serviceBusName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    disableLocalAuth: false
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource serviceBusQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBus
  name: serviceBusQueueName
  properties: {
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P14D'
    lockDuration: 'PT1M'
    maxDeliveryCount: 10
    maxSizeInMegabytes: 1024
  }
}

resource serviceBusAuthorization 'Microsoft.ServiceBus/namespaces/authorizationRules@2024-01-01' = {
  parent: serviceBus
  name: 'claims-portal-app'
  properties: {
    rights: [
      'Listen'
      'Send'
      'Manage'
    ]
  }
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: postgresName
  location: location
  tags: tags
  sku: {
    name: postgresSkuName
    tier: 'Burstable'
  }
  properties: {
    version: postgresVersion
    administratorLogin: postgresAdminLogin
    administratorLoginPassword: postgresAdminPassword
    storage: {
      storageSizeGB: postgresStorageGb
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
    network: {
      publicNetworkAccess: 'Enabled'
    }
  }
}

resource postgresDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: 'claimportal'
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

resource postgresFirewall 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgres
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource containerEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

var postgresConnectionString = 'Host=${postgres.properties.fullyQualifiedDomainName};Port=5432;Database=claimportal;Username=${postgresAdminLogin};Password=${postgresAdminPassword};SSL Mode=Require;Trust Server Certificate=true'
var serviceBusConnectionString = serviceBusAuthorization.listKeys().primaryConnectionString

var postgresSecret = {
  name: 'postgres-connection-string'
  value: postgresConnectionString
}

var serviceBusSecret = {
  name: 'servicebus-connection-string'
  value: serviceBusConnectionString
}

var registrySecret = {
  name: 'ghcr-token'
  value: containerRegistryPassword
}

var honeycombSecret = {
  name: 'honeycomb-api-key'
  value: honeycombApiKey
}

var registrySecrets = usePrivateRegistry ? [registrySecret] : []
var telemetrySecrets = empty(honeycombApiKey) ? [] : [honeycombSecret]
var telemetryEnv = empty(honeycombApiKey)
  ? []
  : [
      {
        name: 'HONEYCOMB_API_KEY'
        secretRef: 'honeycomb-api-key'
      }
    ]
var registries = usePrivateRegistry ? [
  {
    server: containerRegistryServer
    username: containerRegistryUsername
    passwordSecretRef: 'ghcr-token'
  }
] : []

var apiSecrets = concat([postgresSecret, serviceBusSecret], registrySecrets, telemetrySecrets)
var workerSecrets = concat([postgresSecret, serviceBusSecret], registrySecrets, telemetrySecrets)
var mailpitSecrets = concat([serviceBusSecret], registrySecrets)
var webSecrets = registrySecrets

var apiEnv = concat([
  {
    name: 'ASPNETCORE_ENVIRONMENT'
    value: 'Production'
  }
  {
    name: 'ASPNETCORE_URLS'
    value: 'http://+:8080'
  }
  {
    name: 'ConnectionStrings__Claims'
    secretRef: 'postgres-connection-string'
  }
  {
    name: 'Database__MigrateOnStartup'
    value: 'true'
  }
  {
    name: 'Messaging__Transport'
    value: 'AzureServiceBus'
  }
  {
    name: 'Messaging__AzureServiceBus__ConnectionString'
    secretRef: 'servicebus-connection-string'
  }
], telemetryEnv)

var workerEnv = concat([
  {
    name: 'DOTNET_ENVIRONMENT'
    value: 'Production'
  }
  {
    name: 'Database__ConnectionString'
    secretRef: 'postgres-connection-string'
  }
  {
    name: 'Database__MigrateOnStartup'
    value: 'true'
  }
  {
    name: 'Messaging__Transport'
    value: 'AzureServiceBus'
  }
  {
    name: 'Messaging__AzureServiceBus__ConnectionString'
    secretRef: 'servicebus-connection-string'
  }
  {
    name: 'Messaging__ClaimSubmittedQueueName'
    value: serviceBusQueueName
  }
  {
    name: 'SERVICEBUS_CONNECTION'
    secretRef: 'servicebus-connection-string'
  }
  {
    name: 'Smtp__Host'
    value: mailpitName
  }
  {
    name: 'Smtp__Port'
    value: '1025'
  }
  {
    name: 'Smtp__SecureSocketOptions'
    value: 'None'
  }
  {
    name: 'Observability__HealthPort'
    value: '8081'
  }
], telemetryEnv)

var webEnv = [
  {
    name: 'API_UPSTREAM'
    value: '${apiName}:8080'
  }
]

var mailpitEnv = [
  {
    name: 'MP_MAX_MESSAGES'
    value: '100'
  }
  {
    name: 'SERVICEBUS_CONNECTION'
    secretRef: 'servicebus-connection-string'
  }
]

var httpScaleRule = {
  name: 'http'
  type: 'http'
  metadata: {
    concurrentRequests: '10'
  }
}

var serviceBusScaleRule = {
  name: 'servicebus'
  type: 'azure-servicebus'
  metadata: {
    connectionFromEnv: 'SERVICEBUS_CONNECTION'
    queueName: serviceBusQueueName
    messageCount: '1'
  }
}

var workerScaleRules = [
  serviceBusScaleRule
]

var mailpitScaleRules = [
  httpScaleRule
  serviceBusScaleRule
]

// Container Apps consumes the same endpoints the local compose healthchecks use. Liveness is
// process-only; readiness runs the Postgres and broker probes, so a dependency outage removes
// the replica from rotation instead of returning a hard-coded 200.
var apiProbes = [
  {
    type: 'Liveness'
    httpGet: {
      path: '/health/live'
      port: 8080
    }
    initialDelaySeconds: 10
    periodSeconds: 10
    timeoutSeconds: 3
    failureThreshold: 3
  }
  {
    type: 'Readiness'
    httpGet: {
      path: '/health/ready'
      port: 8080
    }
    initialDelaySeconds: 5
    periodSeconds: 10
    timeoutSeconds: 5
    failureThreshold: 3
  }
]

var workerProbes = [
  {
    type: 'Liveness'
    httpGet: {
      path: '/health/live'
      port: 8081
    }
    initialDelaySeconds: 10
    periodSeconds: 10
    timeoutSeconds: 3
    failureThreshold: 3
  }
  {
    type: 'Readiness'
    httpGet: {
      path: '/health/ready'
      port: 8081
    }
    initialDelaySeconds: 5
    periodSeconds: 10
    timeoutSeconds: 5
    failureThreshold: 3
  }
]

module apiApp 'modules/container-app.bicep' = {
  name: 'api-container-app'
  params: {
    name: apiName
    location: location
    environmentId: containerEnvironment.id
    image: apiImage
    ingressEnabled: true
    external: false
    targetPort: 8080
    env: apiEnv
    secrets: apiSecrets
    registries: registries
    minReplicas: 0
    maxReplicas: 2
    scaleRules: [
      httpScaleRule
    ]
    probes: apiProbes
    cpu: '0.25'
    memory: '0.5Gi'
    tags: tags
  }
  dependsOn: [
    postgresDatabase
  ]
}

module workerApp 'modules/container-app.bicep' = {
  name: 'worker-container-app'
  params: {
    name: workerName
    location: location
    environmentId: containerEnvironment.id
    image: workerImage
    // Internal ingress exists only so Container Apps can probe the worker's /health endpoints.
    // It is not external; the consumer still talks to Service Bus, not to HTTP.
    ingressEnabled: true
    external: false
    targetPort: 8081
    env: workerEnv
    secrets: workerSecrets
    registries: registries
    minReplicas: 0
    maxReplicas: 3
    scaleRules: workerScaleRules
    probes: workerProbes
    cpu: '0.25'
    memory: '0.5Gi'
    tags: tags
  }
  dependsOn: [
    postgresDatabase
    serviceBusQueue
  ]
}

module webApp 'modules/container-app.bicep' = {
  name: 'web-container-app'
  params: {
    name: webName
    location: location
    environmentId: containerEnvironment.id
    image: webImage
    ingressEnabled: true
    external: true
    targetPort: 8080
    env: webEnv
    secrets: webSecrets
    registries: registries
    minReplicas: 0
    maxReplicas: 2
    scaleRules: [
      httpScaleRule
    ]
    cpu: '0.25'
    memory: '0.5Gi'
    tags: tags
  }
}

module mailpitApp 'modules/container-app.bicep' = {
  name: 'mailpit-container-app'
  params: {
    name: mailpitName
    location: location
    environmentId: containerEnvironment.id
    image: mailpitImage
    ingressEnabled: true
    external: true
    targetPort: 8025
    additionalPortMappings: [
      {
        targetPort: 1025
        external: false
      }
    ]
    env: mailpitEnv
    secrets: mailpitSecrets
    registries: registries
    minReplicas: 0
    maxReplicas: 1
    scaleRules: mailpitScaleRules
    cpu: '0.25'
    memory: '0.5Gi'
    tags: tags
  }
  dependsOn: [
    serviceBusQueue
  ]
}

resource staticWebApp 'Microsoft.Web/staticSites@2023-12-01' = if (deployStaticWebApp) {
  name: staticWebAppName
  location: location
  tags: tags
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    stagingEnvironmentPolicy: 'Disabled'
  }
}

output resourceGroupName string = resourceGroup().name
output apiAppName string = apiApp.outputs.name
output workerAppName string = workerApp.outputs.name
output webAppName string = webApp.outputs.name
output mailpitAppName string = mailpitApp.outputs.name
output webUrl string = 'https://${webApp.outputs.fqdn}'
output mailpitUrl string = 'https://${mailpitApp.outputs.fqdn}'
output apiInternalFqdn string = apiApp.outputs.fqdn
output postgresFqdn string = postgres.properties.fullyQualifiedDomainName
output serviceBusNamespace string = serviceBus.name
output serviceBusQueue string = serviceBusQueue.name
output logAnalyticsWorkspace string = logAnalytics.name
output staticWebAppDeployed bool = deployStaticWebApp
