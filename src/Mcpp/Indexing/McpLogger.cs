namespace Mcpp.Indexing;

using Microsoft.Extensions.Logging;

/// <summary>
/// Tees diagnostic output to both stderr and a per-invocation log file at
/// %TEMP%\Mcpp\mcp-server-YYYYMMDD-HHmmss.log.
///
/// Uses FileOptions.WriteThrough so every line hits disk immediately —
/// no data loss on crash even without explicit flush.
///
/// Stdout is reserved for MCP JSON-RPC protocol — all diagnostics go through here.
/// </summary>
internal static class McpLogger
{
    private static StreamWriter? _logWriter;
    private static readonly object _lock = new();
    private static string? _logPath;

    /// <summary>Log file path, or null if Init hasn't been called.</summary>
    public static string? LogPath => _logPath;

    /// <summary>
    /// Opens the log file. Call once at startup, before any Log() calls.
    /// Safe to call multiple times (subsequent calls are no-ops).
    /// </summary>
    public static void Init()
    {
        if (_logWriter != null)
            return;

        try
        {
            // Use the user's profile temp dir, not the inherited TEMP env var.
            // Some build systems override TEMP, scattering logs. This keeps
            // them in one stable location.
            var userTemp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logDir = Path.Combine(userTemp, "Temp", "Mcpp");
            Directory.CreateDirectory(logDir);

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var pid = Environment.ProcessId;
            _logPath = Path.Combine(logDir, $"mcp-server-{timestamp}-pid{pid}.log");

            // WriteThrough bypasses OS write cache — every Write hits disk immediately.
            // Critical for crash diagnosis: no buffered lines lost on process death.
            var fs = new FileStream(_logPath, FileMode.Create, FileAccess.Write,
                FileShare.Read, bufferSize: 4096, FileOptions.WriteThrough);
            _logWriter = new StreamWriter(fs) { AutoFlush = true };

            Log("Startup", $"Log file: {_logPath}");
        }
        catch (Exception ex)
        {
            // Can't write to file — fall back to stderr-only (don't crash the server)
            Console.Error.WriteLine($"[McpLogger] Failed to open log file: {ex.Message}");
            _logWriter = null;
        }
    }

    /// <summary>
    /// Writes a timestamped, structured line to both stderr and the log file.
    /// </summary>
    public static void Log(string component, string message, string level = "INFO")
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{component}] {message}";

        Console.Error.WriteLine(line);

        if (_logWriter != null)
        {
            lock (_lock)
            {
                try
                {
                    _logWriter.WriteLine(line);
                }
                catch
                {
                    // Disk full, file locked, etc. — don't crash the server.
                }
            }
        }
    }

    /// <summary>
    /// Flushes and closes the log file. Call on shutdown.
    /// </summary>
    public static void Shutdown()
    {
        lock (_lock)
        {
            _logWriter?.Dispose();
            _logWriter = null;
        }
    }
}

/// <summary>
/// Routes .NET ILogger output through McpLogger so the MCP SDK's built-in
/// tool invocation logging (RequestHandlerCalled/Completed/Exception) appears
/// in both stderr and the log file.
/// </summary>
internal sealed class McpLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new McpLoggerAdapter(categoryName);
    public void Dispose() { }

    private sealed class McpLoggerAdapter(string category) : ILogger
    {
        // Strip the namespace prefix for readability: "ModelContextProtocol.Server.McpServer" → "McpServer"
        private readonly string _tag = category.Contains('.')
            ? category[(category.LastIndexOf('.') + 1)..]
            : category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var level = logLevel switch
            {
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "FATAL",
                _ => "INFO",
            };

            var message = formatter(state, exception);
            McpLogger.Log(_tag, message, level);

            if (exception != null)
                McpLogger.Log(_tag, $"Exception: {exception}", level);
        }
    }
}
