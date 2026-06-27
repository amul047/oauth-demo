"""Main entry point for simple MCP server with Hardcoded oAuth authentication."""

import sys

from mcp_simple_auth.server import main

sys.exit(main())  # type: ignore[call-arg]
