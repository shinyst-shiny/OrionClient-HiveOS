# Orion Client

Ore/Coal mining client with expandable pool and hashing implemention support.

# Build From Source

## 1. Install net8 sdk 
Ubuntu
```
sudo apt-get update && sudo apt-get install -y dotnet-sdk-8.0
```

## 2. Download source
```
git clone https://github.com/SL-x-TnT/OrionClient.git
```

## 3. Publish
```
cd OrionClient && dotnet publish -o Build
```

## 4. Run
```
./Build/OrionClient
```

# Release builds

https://github.com/SL-x-TnT/OrionClient/releases
- **Standalone** versions do not require net8 runtime to be installed
  
## 1. Install net8 runtime (optional)
Ubuntu
```
sudo apt-get update && sudo apt-get install -y dotnet-runtime-8.0
```
Windows
https://dotnet.microsoft.com/en-us/download/dotnet/8.0
