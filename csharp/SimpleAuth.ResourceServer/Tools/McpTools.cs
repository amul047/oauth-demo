using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace SimpleAuth.ResourceServer.Tools;

[McpServerToolType]
public class McpTools
{
    [McpServerTool(Name = "get_time"), Description("Get the current server time")]
    public static object GetTime() => new
    {
        current_time = DateTimeOffset.Now.ToString("O"),
        utc_time = DateTimeOffset.UtcNow.ToString("O"),
        unix_timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        timezone = TimeZoneInfo.Local.DisplayName
    };

    [McpServerTool(Name = "calculator"), Description("Perform mathematical calculations using +, -, *, /, parentheses, and sqrt().")]
    public static object Calculator(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return new { success = false, error = "An expression is required." };
        }

        try
        {
            var normalized = NormalizeExpression(expression);
            var result = Convert.ToDouble(new DataTable().Compute(normalized, string.Empty), CultureInfo.InvariantCulture);
            return new
            {
                success = true,
                expression,
                normalized_expression = normalized,
                result
            };
        }
        catch (Exception ex)
        {
            return new
            {
                success = false,
                expression,
                error = ex.Message
            };
        }
    }

    [McpServerTool(Name = "get_weather"), Description("Get current weather for a city (simulated)")]
    public static object GetWeather(string city, string country = "US")
    {
        var key = string.IsNullOrWhiteSpace(city) ? "unknown" : city.Trim().ToLowerInvariant();
        var seeded = Math.Abs(HashCode.Combine(key, country.ToUpperInvariant()));
        var conditionPool = new[] { "sunny", "partly cloudy", "cloudy", "rainy", "stormy", "foggy" };
        var celsius = 10 + (seeded % 25);
        var humidity = 40 + (seeded % 50);
        var windSpeed = 5 + (seeded % 20);

        return new
        {
            city = string.IsNullOrWhiteSpace(city) ? "Unknown" : city.Trim(),
            country = country.ToUpperInvariant(),
            condition = conditionPool[seeded % conditionPool.Length],
            temperature = new
            {
                celsius,
                fahrenheit = Math.Round((celsius * 9d / 5d) + 32d, 1)
            },
            humidity = $"{humidity}%",
            wind_speed = $"{windSpeed} km/h",
            last_updated = DateTimeOffset.UtcNow.ToString("O"),
            note = "This is simulated demo data."
        };
    }

    private static string NormalizeExpression(string expression)
    {
        if (!Regex.IsMatch(expression, @"^[0-9+\-*/().,\sA-Za-z]+$"))
        {
            throw new InvalidOperationException("Unsupported characters in expression.");
        }

        var normalized = expression;
        while (Regex.IsMatch(normalized, @"sqrt\(([^()]+)\)", RegexOptions.IgnoreCase))
        {
            normalized = Regex.Replace(normalized, @"sqrt\(([^()]+)\)", static match =>
            {
                var innerValue = Convert.ToDouble(new DataTable().Compute(match.Groups[1].Value, string.Empty), CultureInfo.InvariantCulture);
                return Math.Sqrt(innerValue).ToString(CultureInfo.InvariantCulture);
            }, RegexOptions.IgnoreCase);
        }

        return normalized;
    }
}
