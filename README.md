# MCP OAuth Demo

A demonstration of OAuth 2.1 authentication with the Model Context Protocol (MCP), showcasing separated Authorization Server / Resource Server architecture.

Two equivalent implementations are provided — one in Python and one in C#.

---

## Overview

This project demonstrates how to implement OAuth 2.1 authentication for MCP servers and clients using:

- **Authorization Server (AS)**: Handles OAuth flows, dynamic client registration, PKCE, and token issuance
- **Resource Server (RS)**: Validates tokens via introspection and serves protected MCP resources
- **MCP Client**: Authenticates using OAuth and connects to protected MCP servers

---

## Project Structure

```
oauth-demo/
├── python/                         # Python implementation
│   ├── simple-auth/                # Authorization & Resource Server
│   │   ├── mcp_simple_auth/
│   │   │   ├── auth_server.py      # Authorization Server
│   │   │   ├── server.py           # Resource Server
│   │   │   ├── simple_auth_provider.py  # OAuth provider
│   │   │   └── token_verifier.py   # Token introspection
│   │   └── pyproject.toml
│   └── simple-auth-client/         # MCP Client
│       ├── mcp_simple_auth_client/
│       │   └── main.py             # OAuth client
│       └── pyproject.toml
├── csharp/                         # C# implementation
│   ├── SimpleAuth.AuthServer/      # Authorization Server
│   ├── SimpleAuth.ResourceServer/  # Resource Server (MCP)
│   ├── SimpleAuth.Client/          # Console client
│   └── SimpleAuth.sln
└── README.md
```

---

## Architecture

```
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   MCP Client    │───▶│ Authorization   │◀───│  Resource       │
│                 │    │ Server (AS)     │    │  Server (RS)    │
│ - OAuth flow    │    │ - User auth     │    │ - Token verify  │
│ - Token storage │    │ - Token issue   │    │ - MCP tools     │
│ - MCP calls     │    │ - Introspection │    │ - Protected API │
└─────────────────┘    └─────────────────┘    └─────────────────┘
```

Demo credentials (both implementations): **`devloper_harsh` / `admin@2000`**

---

## Python

Ensure you have `python3` & `uv` installed. Then run from the repo root:

### 1. Start the Authorization Server
```bash
cd python/simple-auth
uv run mcp-simple-auth-as --port=9000
```

### 2. Start the Resource Server
```bash
cd python/simple-auth
uv run mcp-simple-auth-rs --port=8001 --auth-server=http://localhost:9000 --transport=streamable-http
```

### 3. Run the Client
```bash
cd python/simple-auth-client
MCP_SERVER_PORT=8001 MCP_TRANSPORT_TYPE=streamable_http uv run mcp-simple-auth-client
```

### Sample Video

https://github.com/user-attachments/assets/d4993185-ff07-4808-afe3-20267be9914c

---

## C#

Ensure you have the [.NET 9 SDK](https://dotnet.microsoft.com/download) installed. Then run from the repo root:

### Build
```bash
dotnet build csharp/SimpleAuth.sln
```

### 1. Start the Authorization Server
```bash
dotnet run --project csharp/SimpleAuth.AuthServer -- --port 9000
```

### 2. Start the Resource Server
```bash
dotnet run --project csharp/SimpleAuth.ResourceServer -- --port 8001 --auth-server http://localhost:9000
```

### 3. Run the Client
```bash
dotnet run --project csharp/SimpleAuth.Client
```

The client opens a browser for login/consent, exchanges the authorization code (PKCE), then connects to the MCP server with an interactive CLI (`list`, `call <tool> [json-args]`, `quit`).

See [`csharp/README.md`](csharp/README.md) for more details.

---

> **Note:** Both implementations use hardcoded demo credentials and in-memory storage. Do not use them in production.

