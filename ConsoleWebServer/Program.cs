using System.Buffers;
using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

var port = 80;
var hostingDirectory = new FileInfo(Assembly.GetEntryAssembly()!.Location).DirectoryName!;
var permittedResourceDirectories = new string[]
{
    hostingDirectory
};

var portOption = new Option<int>(
    aliases: ["--port", "-p"],
    description: "The port that will accept TCP (HTTP) traffic.");

var permittedDirectoriesOption = new Option<string[]>(
    aliases: ["--dirs", "-d"],
    description: "The permitted directories that can be served to HTTP requests.");

var rootCommand = new Command(name: "run", description: "Primitive Console Web Server");

rootCommand.AddOption(portOption);
rootCommand.AddOption(permittedDirectoriesOption);

rootCommand.SetHandler(async (userPort, userDirs) =>
{
    port = userPort;
    permittedResourceDirectories = userDirs;

    Console.WriteLine("Warming up...");

    var allowedResourceDirectories = ScanAllowedResourceDirectories(permittedResourceDirectories);
    var clients = new Dictionary<EndPoint, Socket>();

    try
    {
        var listener = new TcpListener(localaddr: IPAddress.Any, port: port);
        listener.Start();

        Console.WriteLine($"Ready to accept clients on port: {port}");

        await ListenerLoop(listener, allowedResourceDirectories, clients);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to start! {ex}");
    }
},
portOption,
permittedDirectoriesOption);

await rootCommand.InvokeAsync(args);

async Task ListenerLoop(TcpListener listener, HashSet<DirectoryInfo> allowedResourceDirectories, Dictionary<EndPoint, Socket> clients)
{
    while (true)
    {
        var socket = await listener.AcceptSocketAsync();
        var remoteEndpoint = socket.RemoteEndPoint;

        if (remoteEndpoint is not null)
        {
            clients.Add(socket.RemoteEndPoint!, socket);
            _ = HandleNewClient(socket, allowedResourceDirectories, clients);
        }
        else
        {
            Console.WriteLine("Failed to accept socket. No remote endpoint.");
        }
    }
}

async Task HandleNewClient(Socket socket, HashSet<DirectoryInfo> allowedResourceDirectories, Dictionary<EndPoint, Socket> clients)
{
    const int keepAliveTimeoutSeconds = 60;
    using var connectionCts = new CancellationTokenSource();

    try
    {
        Console.WriteLine($"New client connected: {socket.RemoteEndPoint}");

        while (socket.Connected)
        {
            try
            {
                connectionCts.CancelAfter(TimeSpan.FromSeconds(keepAliveTimeoutSeconds));

                var message = await ReadData(socket, Constants.Utf8Encoding, connectionCts.Token);

                if (string.IsNullOrEmpty(message))
                {
                    break;
                }

                connectionCts.CancelAfter(Timeout.Infinite);

                var requestLine = message.Split(Constants.CRLFSeparator, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                Console.WriteLine($"Client {socket.RemoteEndPoint} -> Server: {requestLine}");

                var keepAlive = await TryHandleHttpRequest(message, socket, allowedResourceDirectories, connectionCts.Token);

                if (!keepAlive)
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"Keep-alive timeout ({keepAliveTimeoutSeconds}s) reached for {socket.RemoteEndPoint}");
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling request from {socket.RemoteEndPoint}: {ex.Message}");
                break;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to handle new client ({socket.RemoteEndPoint})! {ex.Message}");
    }
    finally
    {
        try
        {
            socket.Close();
        }
        catch { }

        if (socket.RemoteEndPoint != null && clients.ContainsKey(socket.RemoteEndPoint))
        {
            clients.Remove(socket.RemoteEndPoint);
        }
    }
}

async Task<string> ReadData(Socket socket, Encoding encoding, CancellationToken cancellationToken = default)
{
    const int bufferSize = 4096;
    var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

    try
    {
        var bytesReceived = await socket.ReceiveAsync(buffer.AsMemory(0, bufferSize), cancellationToken);
        var message = encoding.GetString(buffer, 0, bytesReceived);
        return message.TrimEnd(Constants.TrimChars);
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }
}

async Task<bool> TryHandleHttpRequest(string message, Socket socket, HashSet<DirectoryInfo> allowedResourceDirectories, CancellationToken cancellationToken = default)
{
    if (message.Length == 0)
    {
        socket.Close();
        return false;
    }

    var lines = message.Split(Constants.CRLFSeparator, StringSplitOptions.RemoveEmptyEntries);

    if (lines.Length <= 0)
    {
        socket.Close();
        return false;
    }

    var httpRequestLine = lines[0];
    var splitHttpRequestLine = httpRequestLine.Split(Constants.SpaceSeparator, StringSplitOptions.RemoveEmptyEntries);

    /*
     * [0] - HTTP Method (GET)
     * [1] - Resource Path (/)
     * [2] - HTTP Version (HTTP/1.1)
     */
    if (splitHttpRequestLine.Length < 3)
    {
        socket.Close();
        return false;
    }

    var method = splitHttpRequestLine[0];
    var resourceLocator = splitHttpRequestLine[1];
    var httpVersion = splitHttpRequestLine[2];

    var keepAlive = CheckKeepAlive(lines);

    switch (method.ToUpperInvariant())
    {
        case Constants.MethodGet:
            await HandleHttpGetRequest(resourceLocator, httpVersion, splitHttpRequestLine, socket, allowedResourceDirectories, keepAlive, cancellationToken);
            return keepAlive;
        default:
            if (!keepAlive)
                socket.Close();
            return keepAlive;
    }
}

async Task HandleHttpGetRequest(string resourceLocator, string httpVersion, string[] splitHttpRequestLine, Socket socket, HashSet<DirectoryInfo> allowedResourceDirectories, bool keepAlive, CancellationToken cancellationToken = default)
{
    try
    {
        var queryIndex = resourceLocator.IndexOf('?');
        var pathOnly = queryIndex > -1
            ? resourceLocator[..queryIndex]
            : resourceLocator;

        pathOnly = pathOnly.Replace("//", "/");

        DirectoryInfo? matchingDirectory = null;
        FileInfo? matchingFile = null;

        foreach (var allowedDir in allowedResourceDirectories)
        {
            var fullPath = Path.Combine(allowedDir.FullName, pathOnly.TrimStart('/'));

            if (pathOnly.EndsWith("/"))
            {
                var directory = new DirectoryInfo(fullPath);
                if (directory.Exists)
                {
                    matchingDirectory = directory;
                    break;
                }
            }
            else
            {
                var file = new FileInfo(fullPath);
                if (file.Exists)
                {
                    matchingFile = file;
                    break;
                }
            }
        }

        if (matchingDirectory != null && pathOnly.EndsWith("/"))
        {
            var directoryListing = GenerateDirectoryListing(matchingDirectory, pathOnly, allowedResourceDirectories);
            await SendResponse(socket, Constants.StatusOk, "text/html", directoryListing, keepAlive, cancellationToken);
        }
        else if (matchingFile != null)
        {
            await SendFileResponse(socket, matchingFile, keepAlive, cancellationToken);
        }
        else
        {
            bool indexFound = false;
            if (pathOnly.EndsWith("/"))
            {
                foreach (var allowedDir in allowedResourceDirectories)
                {
                    var indexPath = Path.Combine(allowedDir.FullName, pathOnly.TrimStart('/'), "index.html");
                    var indexFile = new FileInfo(indexPath);
                    if (indexFile.Exists)
                    {
                        await SendFileResponse(socket, indexFile, keepAlive, cancellationToken);
                        indexFound = true;
                        break;
                    }
                }
            }

            if (!indexFound)
            {
                await SendResponse(socket, Constants.StatusNotFound, "text/plain", "404 Not Found", keepAlive, cancellationToken);
            }
        }
    }
    catch (SecurityException)
    {
        await SendResponse(socket, Constants.StatusForbidden, "text/plain", "403 Forbidden", keepAlive, cancellationToken);
    }
    finally
    {
    }
}

bool CheckKeepAlive(string[] headers)
{
    foreach (var header in headers)
    {
        if (header.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
        {
            return header.Contains(Constants.ConnectionKeepAlive, StringComparison.OrdinalIgnoreCase);
        }
    }
    return false;
}

async Task SendResponse(Socket socket, string statusCode, string contentType, string content, bool keepAlive, CancellationToken cancellationToken = default)
{
    var contentBytes = Constants.Utf8Encoding.GetBytes(content);

    var headerBuilder = new StringBuilder(256);
    headerBuilder.Append(Constants.HttpVersion11)
                 .Append(' ')
                 .Append(statusCode)
                 .Append(Constants.CRLF)
                 .Append("Content-Type: ")
                 .Append(contentType)
                 .Append(Constants.CRLF)
                 .Append("Content-Length: ")
                 .Append(contentBytes.Length)
                 .Append(Constants.CRLF)
                 .Append("Connection: ")
                 .Append(keepAlive ? Constants.ConnectionKeepAlive : Constants.ConnectionClose)
                 .Append(Constants.CRLF);

    if (keepAlive)
    {
        headerBuilder.Append("Keep-Alive: timeout=60, max=100")
                     .Append(Constants.CRLF);
    }

    headerBuilder.Append(Constants.CRLF);

    var headers = headerBuilder.ToString();
    var headerBytes = Constants.Utf8Encoding.GetBytes(headers);

    Console.WriteLine($"Server -> Client {socket.RemoteEndPoint}: {headers.TrimEnd()}");

    await socket.SendAsync(headerBytes, cancellationToken);
    await socket.SendAsync(contentBytes, cancellationToken);
}

async Task SendFileResponse(Socket socket, FileInfo file, bool keepAlive, CancellationToken cancellationToken = default)
{
    var contentType = GetContentType(file.Extension);

    var headerBuilder = new StringBuilder(256);
    headerBuilder.Append(Constants.HttpVersion11)
                 .Append(' ')
                 .Append(Constants.StatusOk)
                 .Append(Constants.CRLF)
                 .Append("Content-Type: ")
                 .Append(contentType)
                 .Append(Constants.CRLF)
                 .Append("Content-Length: ")
                 .Append(file.Length)
                 .Append(Constants.CRLF)
                 .Append("Connection: ")
                 .Append(keepAlive ? Constants.ConnectionKeepAlive : Constants.ConnectionClose)
                 .Append(Constants.CRLF);

    if (keepAlive)
    {
        headerBuilder.Append("Keep-Alive: timeout=60, max=100")
                     .Append(Constants.CRLF);
    }

    headerBuilder.Append(Constants.CRLF);

    var headers = headerBuilder.ToString();
    var headerBytes = Constants.Utf8Encoding.GetBytes(headers);

    Console.WriteLine($"Server -> Client {socket.RemoteEndPoint}: {headers.TrimEnd()}");

    await socket.SendAsync(headerBytes, cancellationToken);
    using var fileStream = file.OpenRead();
    const int bufferSize = 8192;
    var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

    try
    {
        int bytesRead;
        while ((bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken)) > 0)
        {
            await socket.SendAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }
}

string GetContentType(string fileExtension)
{
    return fileExtension.ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html",
        ".css" => "text/css",
        ".js" => "application/javascript",
        ".json" => "application/json",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".txt" => "text/plain",
        ".xml" => "application/xml",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream"
    };
}

string GenerateDirectoryListing(DirectoryInfo directory, string requestPath, HashSet<DirectoryInfo> allowedResourceDirectories)
{
    var html = new StringBuilder();
    html.AppendLine("<!DOCTYPE html>");
    html.AppendLine("<html>");
    html.AppendLine("<head>");
    html.AppendLine($"<title>Index of {requestPath}</title>");
    html.AppendLine("</head>");
    html.AppendLine("<body>");
    html.AppendLine($"<h1>Index of {requestPath}</h1>");
    html.AppendLine("<hr>");
    html.AppendLine("<pre>");

    if (requestPath != "/" && CanNavigateToParent(directory, allowedResourceDirectories))
    {
        var parentPath = requestPath.TrimEnd('/');
        var lastSlash = parentPath.LastIndexOf('/');
        var parentRequestPath = lastSlash <= 0 ? "/" : parentPath[..lastSlash] + "/";
        html.AppendLine($"<a href=\"{parentRequestPath}\">../</a>");
    }

    try
    {
        var directories = directory.GetDirectories()
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

        var pathBuilder = new StringBuilder(requestPath.Length + 64);

        foreach (var dir in directories)
        {
            pathBuilder.Clear();
            pathBuilder.Append(requestPath);
            if (!requestPath.EndsWith("/"))
                pathBuilder.Append('/');
            pathBuilder.Append(dir.Name);
            pathBuilder.Append('/');

            html.Append("<a href=\"")
                .Append(pathBuilder.ToString())
                .Append("\">")
                .Append(dir.Name)
                .AppendLine("/</a>");
        }

        var files = directory.GetFiles()
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            pathBuilder.Clear();
            pathBuilder.Append(requestPath);
            if (!requestPath.EndsWith("/"))
                pathBuilder.Append('/');
            pathBuilder.Append(file.Name);

            var formattedSize = FormatFileSize(file.Length);
            var lastModified = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm");

            html.Append("<a href=\"")
                .Append(pathBuilder.ToString())
                .Append("\">")
                .Append(file.Name)
                .Append("</a>    ")
                .Append(lastModified)
                .Append("    ")
                .AppendLine(formattedSize);
        }
    }
    catch (UnauthorizedAccessException)
    {
        html.AppendLine("Access denied to this directory.");
    }

    html.AppendLine("</pre>");
    html.AppendLine("<hr>");
    html.AppendLine("</body>");
    html.AppendLine("</html>");

    return html.ToString();
}

bool CanNavigateToParent(DirectoryInfo directory, HashSet<DirectoryInfo> allowedResourceDirectories)
{
    var parent = directory.Parent;
    if (parent == null) return false;

    return allowedResourceDirectories.Any(allowedDirectory =>
        parent.FullName.Equals(allowedDirectory.FullName) ||
        parent.FullName.StartsWith(allowedDirectory.FullName + Path.DirectorySeparatorChar));
}

string FormatFileSize(long bytes)
{
    if (bytes == 0) return "0B";

    int counter = 0;
    decimal number = bytes;

    while (counter < Constants.FileSizeSuffixes.Length - 1 && Math.Round(number / 1024) >= 1)
    {
        number /= 1024;
        counter++;
    }

    var sb = new StringBuilder(8);
    sb.Append(number.ToString("0.#"));
    sb.Append(Constants.FileSizeSuffixes[counter]);
    return sb.ToString();
}

HashSet<DirectoryInfo> ScanAllowedResourceDirectories(IEnumerable<string> permittedResourceDirectories)
{
    var hs = new HashSet<DirectoryInfo>();
    foreach (var dir in permittedResourceDirectories)
    {
        hs.Add(new DirectoryInfo(dir));

        var subDirs = Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories);
        foreach (var subDir in subDirs)
        {
            hs.Add(new DirectoryInfo(subDir));
        }
    }
    return hs;
}

internal static class Constants
{
    // HTTP Methods (RFC 9110 Section 9)
    public const string MethodGet = "GET";

    // HTTP Status Codes (RFC 9110 Section 15)
    public const string StatusOk = "200 OK";
    public const string StatusNotFound = "404 Not Found";
    public const string StatusForbidden = "403 Forbidden";

    // HTTP Protocol (RFC 9112 Section 2.1)
    public const string HttpVersion11 = "HTTP/1.1";

    // HTTP Line Termination (RFC 9112 Section 2.2)
    public const string CRLF = "\r\n";

    // Connection Header Values (RFC 9110 Section 7.6.1)
    public const string ConnectionKeepAlive = "keep-alive";
    public const string ConnectionClose = "close";

    public static readonly Encoding Utf8Encoding = Encoding.UTF8;
    public static readonly string[] CRLFSeparator = ["\r\n"];
    public static readonly char[] SpaceSeparator = [' '];
    public static readonly char[] TrimChars = ['\0', ' ', '\r', '\n'];
    public static readonly string[] FileSizeSuffixes = ["B", "KB", "MB", "GB", "TB"];
}
