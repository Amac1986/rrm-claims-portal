using './main.bicep'

param location = 'canadacentral'
param namePrefix = 'rrmclaims'
param postgresAdminLogin = 'claimportal'
param postgresAdminPassword = readEnvironmentVariable('POSTGRES_ADMIN_PASSWORD')
// Images default to the ACR this template creates; override only for a different registry.
param containerRegistryServer = readEnvironmentVariable('CONTAINER_REGISTRY_SERVER', '')
param containerRegistryUsername = readEnvironmentVariable('CONTAINER_REGISTRY_USERNAME', '')
param containerRegistryPassword = readEnvironmentVariable('CONTAINER_REGISTRY_PASSWORD', '')
param deployStaticWebApp = true
param staticWebAppLocation = 'centralus'
