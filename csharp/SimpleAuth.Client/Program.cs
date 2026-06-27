using SimpleAuth.Client;

var portText = Environment.GetEnvironmentVariable("MCP_SERVER_PORT") ?? "8001";
if (args.Length >= 2 && args[0] == "--port")
{
    portText = args[1];
}

var resourceServerUrl = $"http://localhost:{portText}";
Console.WriteLine($"SimpleAuth C# client connecting to {resourceServerUrl}");

var client = new OAuthMcpClient(resourceServerUrl);
await client.RunAsync();
