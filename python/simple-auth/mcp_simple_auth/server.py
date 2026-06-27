"""
MCP Resource Server with Token Introspection.

This server validates tokens via Authorization Server introspection and serves MCP resources.
Demonstrates RFC 9728 Protected Resource Metadata for AS/RS separation.

NOTE: this is a simplified example for demonstration purposes.
This is not a production-ready implementation.
"""

import datetime
import logging
import random
from typing import Any, Literal

import click
from pydantic import AnyHttpUrl
from pydantic_settings import BaseSettings, SettingsConfigDict

from mcp.server.auth.settings import AuthSettings
from mcp.server.fastmcp.server import FastMCP

from .token_verifier import IntrospectionTokenVerifier

logger = logging.getLogger(__name__)


class ResourceServerSettings(BaseSettings):
    """Settings for the MCP Resource Server."""

    model_config = SettingsConfigDict(env_prefix="MCP_RESOURCE_")

    # Server settings
    host: str = "localhost"
    port: int = 8001
    server_url: AnyHttpUrl = AnyHttpUrl("http://localhost:8001")

    # Authorization Server settings
    auth_server_url: AnyHttpUrl = AnyHttpUrl("http://localhost:9000")
    auth_server_introspection_endpoint: str = "http://localhost:9000/introspect"
    # No user endpoint needed - we get user data from token introspection

    # MCP settings
    mcp_scope: str = "user"

    # RFC 8707 resource validation
    oauth_strict: bool = False

    # TODO(Marcelo): Is this even needed? I didn't have time to check.
    def __init__(self, **data: Any):
        """Initialize settings with values from environment variables."""
        super().__init__(**data)


def create_resource_server(settings: ResourceServerSettings) -> FastMCP:
    """
    Create MCP Resource Server with token introspection.

    This server:
    1. Provides protected resource metadata (RFC 9728)
    2. Validates tokens via Authorization Server introspection
    3. Serves MCP tools and resources
    """
    # Create token verifier for introspection with RFC 8707 resource validation
    token_verifier = IntrospectionTokenVerifier(
        introspection_endpoint=settings.auth_server_introspection_endpoint,
        server_url=str(settings.server_url),
        validate_resource=settings.oauth_strict,  # Only validate when --oauth-strict is set
    )

    # Create FastMCP server as a Resource Server
    app = FastMCP(
        name="MCP Resource Server",
        instructions="Resource Server that validates tokens via Authorization Server introspection",
        host=settings.host,
        port=settings.port,
        debug=True,
        # Auth configuration for RS mode
        token_verifier=token_verifier,
        auth=AuthSettings(
            issuer_url=settings.auth_server_url,
            required_scopes=[settings.mcp_scope],
            resource_server_url=settings.server_url,
        ),
    )

    @app.tool()
    async def get_time() -> dict[str, Any]:
        """
        Get the current server time.

        This tool demonstrates that system information can be protected
        by OAuth authentication. User must be authenticated to access it.
        """

        now = datetime.datetime.now()

        return {
            "current_time": now.isoformat(),
            "timezone": "UTC",  # Simplified for demo
            "timestamp": now.timestamp(),
            "formatted": now.strftime("%Y-%m-%d %H:%M:%S"),
        }

    @app.tool()
    async def calculator(expression: str) -> dict[str, Any]:
        """
        Perform mathematical calculations.
        
        This tool evaluates mathematical expressions safely.
        Supports basic arithmetic operations (+, -, *, /), parentheses, and common math functions.
        
        Args:
            expression: Mathematical expression to evaluate (e.g., "2 + 3 * 4", "sqrt(16)", "sin(3.14159/2)")
        """
        import math
        
        try:
            # Create a safe namespace for evaluation
            safe_dict = {
                "__builtins__": {},
                # Math constants
                "pi": math.pi,
                "e": math.e,
                # Basic math functions
                "abs": abs,
                "round": round,
                "min": min,
                "max": max,
                "sum": sum,
                # Advanced math functions
                "sqrt": math.sqrt,
                "pow": pow,
                "exp": math.exp,
                "log": math.log,
                "log10": math.log10,
                "sin": math.sin,
                "cos": math.cos,
                "tan": math.tan,
                "asin": math.asin,
                "acos": math.acos,
                "atan": math.atan,
                "degrees": math.degrees,
                "radians": math.radians,
                "ceil": math.ceil,
                "floor": math.floor,
                "factorial": math.factorial,
            }
            
            # Evaluate the expression safely
            result = eval(expression, safe_dict, {})
            
            return {
                "expression": expression,
                "result": result,
                "type": type(result).__name__,
                "success": True
            }
            
        except ZeroDivisionError:
            return {
                "expression": expression,
                "error": "Division by zero",
                "success": False
            }
        except (SyntaxError, NameError, TypeError, ValueError) as e:
            return {
                "expression": expression,
                "error": f"Invalid expression: {str(e)}",
                "success": False
            }
        except Exception as e:
            return {
                "expression": expression,
                "error": f"Calculation error: {str(e)}",
                "success": False
            }

    @app.tool()
    async def get_weather(city: str, country: str = "US") -> dict[str, Any]:
        """
        Get current weather information for a city.
        
        This is a demo weather tool that returns simulated weather data.
        In a real implementation, this would connect to a weather API like OpenWeatherMap.
        
        Args:
            city: Name of the city to get weather for
            country: Country code (default: "US")
        """
        # Simulate weather data (in a real implementation, you'd call a weather API)
        weather_conditions = [
            "sunny", "partly cloudy", "cloudy", "rainy", "stormy", "snowy", "foggy"
        ]
        
        # Generate realistic temperature ranges based on common city names
        temp_ranges = {
            "new york": (15, 25),
            "london": (8, 18),
            "tokyo": (12, 22),
            "sydney": (18, 28),
            "mumbai": (25, 35),
            "cairo": (20, 35),
            "moscow": (-5, 10),
            "reykjavik": (0, 8),
        }
        
        city_lower = city.lower()
        temp_range = temp_ranges.get(city_lower, (10, 25))  # Default range
        
        # Generate simulated data
        temperature = random.randint(temp_range[0], temp_range[1])
        condition = random.choice(weather_conditions)
        humidity = random.randint(40, 90)
        wind_speed = random.randint(5, 25)
        
        return {
            "city": city.title(),
            "country": country.upper(),
            "temperature": {
                "celsius": temperature,
                "fahrenheit": round(temperature * 9/5 + 32, 1)
            },
            "condition": condition,
            "humidity": f"{humidity}%",
            "wind_speed": f"{wind_speed} km/h",
            "last_updated": datetime.datetime.now().isoformat(),
            "note": "This is simulated weather data for demonstration purposes"
        }

    return app


@click.command()
@click.option("--port", default=8001, help="Port to listen on")
@click.option("--auth-server", default="http://localhost:9000", help="Authorization Server URL")
@click.option(
    "--transport",
    default="streamable-http",
    type=click.Choice(["sse", "streamable-http"]),
    help="Transport protocol to use ('sse' or 'streamable-http')",
)
@click.option(
    "--oauth-strict",
    is_flag=True,
    help="Enable RFC 8707 resource validation",
)
def main(port: int, auth_server: str, transport: Literal["sse", "streamable-http"], oauth_strict: bool) -> int:
    """
    Run the MCP Resource Server.

    This server:
    - Provides RFC 9728 Protected Resource Metadata
    - Validates tokens via Authorization Server introspection
    - Serves MCP tools requiring authentication

    Must be used with a running Authorization Server.
    """
    logging.basicConfig(level=logging.INFO)

    try:
        # Parse auth server URL
        auth_server_url = AnyHttpUrl(auth_server)

        # Create settings
        host = "localhost"
        server_url = f"http://{host}:{port}"
        settings = ResourceServerSettings(
            host=host,
            port=port,
            server_url=AnyHttpUrl(server_url),
            auth_server_url=auth_server_url,
            auth_server_introspection_endpoint=f"{auth_server}/introspect",
            oauth_strict=oauth_strict,
        )
    except ValueError as e:
        logger.error(f"Configuration error: {e}")
        logger.error("Make sure to provide a valid Authorization Server URL")
        return 1

    try:
        mcp_server = create_resource_server(settings)

        logger.info(f"🚀 MCP Resource Server running on {settings.server_url}")
        logger.info(f"🔑 Using Authorization Server: {settings.auth_server_url}")

        # Run the server - this should block and keep running
        mcp_server.run(transport=transport)
        logger.info("Server stopped")
        return 0
    except Exception:
        logger.exception("Server error")
        return 1


if __name__ == "__main__":
    main()  # type: ignore[call-arg]
