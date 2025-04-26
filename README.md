# Orion Client

Ore/Coal mining client with expandable pool and hashing implementation support.

# Standalone versions (no dependencies required)
- https://github.com/shinyst-shiny/OrionClient-HiveOS/releases
  
# Build From Source

## 1. Install net8 sdk 
Ubuntu (22.04 and later)
```
sudo apt-get update && sudo apt-get install -y dotnet-sdk-8.0
```
Windows
- https://dotnet.microsoft.com/en-us/download/dotnet/8.0

## 1a. Install git (windows only)
Windows
- https://git-scm.com/downloads

## 2. Download source
```
git clone --recurse-submodules https://github.com/SL-x-TnT/OrionClient.git
```

## 3. Publish
```
cd OrionClient
dotnet publish -o Build -f net8.0
```

## 3a. Pulling new changes
While in the `OrionClient` folder
```
git pull --recurse-submodules
dotnet publish -o Build -f net8.0
```

## 4. Run
Ubuntu
```
./Build/OrionClient
```

Windows
```
.\Build\OrionClient.exe
```
