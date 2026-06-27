# SimpleAuth C# OAuth 2.1 MCP Demo

This directory contains a C# equivalent of the Python OAuth 2.1 MCP demo.

## Components

- `SimpleAuth.AuthServer` - OAuth 2.1 authorization server on port `9000`
- `SimpleAuth.ResourceServer` - MCP resource server on port `8001`
- `SimpleAuth.Client` - console client with browser-based OAuth login

Demo credentials:

- Username: `devloper_harsh`
- Password: `admin@2000`

## Build

```bash
dotnet build csharp/SimpleAuth.sln
```

## Run

Open three terminals from the repository root.

### 1. Authorization server

```bash
dotnet run --project csharp/SimpleAuth.AuthServer -- --port 9000
```

### 2. Resource server

```bash
dotnet run --project csharp/SimpleAuth.ResourceServer -- --port 8001 --auth-server http://localhost:9000
```

### 3. Client

```bash
dotnet run --project csharp/SimpleAuth.Client
```

The client starts a local callback listener at `http://localhost:3030/callback`, opens the browser for login/consent, exchanges the authorization code with PKCE, then connects to the MCP server.

## Client commands

- `list` - list available MCP tools
- `call get_time`
- `call calculator {"expression":"sqrt(144) + 8 / 2"}`
- `call get_weather {"city":"London","country":"GB"}`
- `quit`

If the browser cannot be opened automatically, copy the printed authorization URL into a browser manually.
