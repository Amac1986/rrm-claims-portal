@description('Container app name. It is also used as the internal DNS hostname in this environment.')
param name string

@description('Azure region for the container app.')
param location string

@description('Resource ID of the Container Apps managed environment.')
param environmentId string

@description('Container image to run.')
param image string

@description('Enable ingress for this app.')
param ingressEnabled bool = false

@description('Expose the app outside the Container Apps environment.')
param external bool = false

@description('Allow plain HTTP ingress inside the Container Apps environment. Required when another app proxies to this one over HTTP.')
param allowInsecure bool = false

@description('Container port targeted by HTTP ingress.')
param targetPort int = 0

@description('Additional TCP port mappings, for example Mailpit SMTP on 1025.')
param additionalPortMappings array = []

@description('Environment variables for the container.')
param env array = []

@description('Container app secrets, referenced by env entries with secretRef.')
param secrets array = []

@description('Private registry credentials, if the images are not public.')
param registries array = []

@description('Minimum replicas. Keep this at zero for the Consumption free grant.')
param minReplicas int = 0

@description('Maximum replicas.')
param maxReplicas int = 3

@description('KEDA and HTTP scale rules.')
param scaleRules array = []

@description('Container health probes (liveness/readiness/startup).')
param probes array = []

@description('vCPU allocation for the container.')
param cpu string = '0.25'

@description('Memory allocation for the container.')
param memory string = '0.5Gi'

@description('Tags applied to the container app.')
param tags object = {}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: ingressEnabled ? {
        external: external
        targetPort: targetPort
        transport: 'http'
        allowInsecure: allowInsecure
        additionalPortMappings: additionalPortMappings
      } : null
      secrets: secrets
      registries: registries
    }
    template: {
      containers: [
        {
          name: name
          image: image
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: env
          probes: probes
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: scaleRules
      }
    }
  }
}

output id string = app.id
output name string = app.name
output fqdn string = app.properties.latestRevisionFqdn
