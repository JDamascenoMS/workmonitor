@description('Name of the DevTest Lab')
param labName string = 'ft-monitor-lab'

@description('Name of the VM inside the lab')
param vmName string = 'ft-monitor-vm'

@description('Azure region for all resources')
param location string = resourceGroup().location

@description('VM admin username')
param adminUsername string

@secure()
@description('VM admin password')
param adminPassword string

@description('VM size')
param vmSize string = 'Standard_B2s'

// DevTest Lab
resource lab 'Microsoft.DevTestLab/labs@2018-09-15' = {
  name: labName
  location: location
  properties: {
    labStorageType: 'Standard'
  }
}

// Virtual Network created automatically by the lab
resource labVnet 'Microsoft.DevTestLab/labs/virtualnetworks@2018-09-15' = {
  parent: lab
  name: '${labName}Vnet'
  properties: {
    allowedSubnets: [
      {
        resourceId: resourceId('Microsoft.Network/virtualNetworks/subnets', '${labName}Vnet', '${labName}Subnet')
        labSubnetName: '${labName}Subnet'
        allowPublicIp: 'Allow'
      }
    ]
    subnetOverrides: [
      {
        resourceId: resourceId('Microsoft.Network/virtualNetworks/subnets', '${labName}Vnet', '${labName}Subnet')
        labSubnetName: '${labName}Subnet'
        useInVmCreationPermission: 'Allow'
        usePublicIpAddressPermission: 'Allow'
      }
    ]
  }
}

// Windows Server 2022 VM in the lab
resource vm 'Microsoft.DevTestLab/labs/virtualmachines@2018-09-15' = {
  parent: lab
  name: vmName
  location: location
  properties: {
    size: vmSize
    userName: adminUsername
    password: adminPassword
    galleryImageReference: {
      offer: 'WindowsServer'
      publisher: 'MicrosoftWindowsServer'
      sku: '2022-datacenter-azure-edition'
      osType: 'Windows'
      version: 'latest'
    }
    allowClaim: false
    labVirtualNetworkId: labVnet.id
    labSubnetName: '${labName}Subnet'
    storageType: 'Standard'
    // Artifact: install IIS and ASP.NET Core Hosting Bundle
    artifacts: [
      {
        artifactId: resourceId('Microsoft.DevTestLab/labs/artifactSources/artifacts', labName, 'public repo', 'windows-iis')
      }
    ]
  }
}

output labId string = lab.id
output vmId string = vm.id
output labName string = lab.name
output vmName string = vm.name
