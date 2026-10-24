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

@description('API image, including tag. Empty uses the Azure Container Registry this template creates.')
param apiImage string = ''

@description('Worker image, including tag. Empty uses the Azure Container Registry this template creates.')
param workerImage string = ''

@description('Web image, including tag. Empty uses the Azure Container Registry this template creates.')
param webImage string = ''

@description('Mailpit image, including tag.')
param mailpitImage string = 'axllent/mailpit:v1.27'

@description('Service Bus queue consumed by the notification worker and used by KEDA.')
param serviceBusQueueName string = 'claim-submitted-notifications'

@description('Container registry login server. Empty uses the ACR this template creates.')
param containerRegistryServer string = ''

@description('Container registry username. Empty uses the ACR admin user this template creates.')
param containerRegistryUsername string = ''

@description('Container registry password. Empty uses the ACR admin password this template creates.')
@secure()
param containerRegistryPassword string = ''

@description('Honeycomb API key. Leave empty for a deployment without tracing; supply from a pipeline secret, never commit it.')
@secure()
param honeycombApiKey string = ''

@description('Create the free-tier Static Web App resource for the React app.')
param deployStaticWebApp bool = true

@description('Region for the Static Web App. Static Web Apps is not offered in every region; centralus is used for a canadacentral deployment.')
param staticWebAppLocation string = 'centralus'

var suffix = uniqueString(resourceGroup().id)
var containerRegistryName = toLower('${namePrefix}acr${suffix}')
var logAnalyticsName = toLower('${namePrefix}-logs-${suffix}')
var serviceBusName = toLower('${namePrefix}-sb-${suffix}')
var postgresName = toLower('${namePrefix}-pg-${suffix}')
var environmentName = toLower('${namePrefix}-env-${suffix}')
var apiName = toLower('${namePrefix}-api')
var workerName = toLower('${namePrefix}-worker')
var webName = toLower('${namePrefix}-web')
var mailpitName = toLower('${namePrefix}-mailpit')
var staticWebAppName = toLower('${namePrefix}-swa-${suffix}')

// The repository is private and its GHCR packages are private, so Container Apps cannot pull
// them anonymously. A Basic ACR is created and its admin credential is used for the pulls:
// roughly $5/month, and it keeps the deployment self-contained with no token shared by hand.
resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: containerRegistryName
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: true
    publicNetworkAccess: 'Enabled'
  }
}

// Deterministic where possible: an ACR login server is always <name>.azurecr.io and its admin
// username is the registry name. Only the password needs listCredentials, which keeps the
// rendered image names readable in what-if instead of showing a runtime expression.
var registryServer = empty(containerRegistryServer) ? '${containerRegistryName}.azurecr.io' : containerRegistryServer
var registryUsername = empty(containerRegistryUsername) ? containerRegistryName : containerRegistryUsername
var registryPassword = empty(containerRegistryPassword) ? containerRegistry.listCredentials().passwords[0].value : containerRegistryPassword

var apiImageResolved = empty(apiImage) ? '${registryServer}/api:latest' : apiImage
var workerImageResolved = empty(workerImage) ? '${registryServer}/worker:latest' : workerImage
var webImageResolved = empty(webImage) ? '${registryServer}/web:latest' : webImage

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
  name: 'registry-password'
  value: registryPassword
}

var honeycombSecret = {
  name: 'honeycomb-api-key'
  value: honeycombApiKey
}

var registrySecrets = [registrySecret]
var telemetrySecrets = empty(honeycombApiKey) ? [] : [honeycombSecret]
var telemetryEnv = empty(honeycombApiKey)
  ? []
  : [
      {
        name: 'HONEYCOMB_API_KEY'
        secretRef: 'honeycomb-api-key'
      }
    ]
var registries = [
  {
    server: registryServer
    username: registryUsername
    passwordSecretRef: 'registry-password'
  }
]

var apiSecrets = concat([postgresSecret, serviceBusSecret], registrySecrets, telemetrySecrets)
var workerSecrets = concat([postgresSecret, serviceBusSecret], registrySecrets, telemetrySecrets)
var mailpitSecrets = [serviceBusSecret]
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
    // Container Apps routes by Host header, so the nginx proxy must send the API's internal
    // FQDN rather than the browser's Host or a bare service name.
    name: 'API_UPSTREAM'
    value: '${apiName}.internal.${containerEnvironment.properties.defaultDomain}'
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

// Container Apps 2024-03-01 requires the typed shape: HTTP rules under `http`, KEDA rules
// under `custom` with the scaler type. The older flat `{ type, metadata }` shape is rejected.
var httpScaleRule = {
  name: 'http'
  http: {
    metadata: {
      concurrentRequests: '10'
    }
  }
}

var serviceBusScaleRule = {
  name: 'servicebus'
  custom: {
    type: 'azure-servicebus'
    metadata: {
      connectionFromEnv: 'SERVICEBUS_CONNECTION'
      queueName: serviceBusQueueName
      messageCount: '1'
    }
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
    image: apiImageResolved
    ingressEnabled: true
    external: false
    // The web app proxies to the API over plain HTTP inside the environment; without this the
    // in-environment proxy receives a 301 to HTTPS and the POST never arrives.
    allowInsecure: true
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
    image: workerImageResolved
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
    image: webImageResolved
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
    // Mailpit is pulled from Docker Hub, so the ACR credential does not apply to it.
    registries: []
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
  location: staticWebAppLocation
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
output containerRegistryName string = containerRegistry.name
output containerRegistryLoginServer string = registryServer
output staticWebAppDeployed bool = deployStaticWebApp
