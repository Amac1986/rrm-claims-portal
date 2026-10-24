using './main.bicep'

param location = 'canadacentral'
param namePrefix = 'rrmclaims'
param postgresAdminLogin = 'claimportal'
param postgresAdminPassword = readEnvironmentVariable('POSTGRES_ADMIN_PASSWORD')
param apiImage = 'ghcr.io/amac1986/rrm-claims-portal/api:latest'
param workerImage = 'ghcr.io/amac1986/rrm-claims-portal/worker:latest'
param webImage = 'ghcr.io/amac1986/rrm-claims-portal/web:latest'
param usePrivateRegistry = false
param containerRegistryUsername = readEnvironmentVariable('GHCR_USER', '')
param containerRegistryPassword = readEnvironmentVariable('GHCR_TOKEN', '')
param deployStaticWebApp = true
