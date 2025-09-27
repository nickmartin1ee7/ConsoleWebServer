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

        // Handle multiple requests on the same connection (keep-alive support)
        while (socket.Connected)
        {
            try
            {
                // Set timeout for keep-alive connections (60 seconds)
                connectionCts.CancelAfter(TimeSpan.FromSeconds(keepAliveTimeoutSeconds));
                
                var message = await ReadData(socket, Encoding.UTF8, connectionCts.Token);

                // If we get an empty message, client likely disconnected
                if (string.IsNullOrEmpty(message))
                {
                    break;
                }

                // Reset the timeout for the next request after successful read
                connectionCts.CancelAfter(Timeout.Infinite);

                // Log only the request line, not the full content
                var requestLine = message.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                Console.WriteLine($"Client {socket.RemoteEndPoint} -> Server: {requestLine}");

                var keepAlive = await TryHandleHttpRequest(message, socket, allowedResourceDirectories, connectionCts.Token);

                // If keep-alive is false, close the connection
                if (!keepAlive)
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                // Timeout occurred - close the connection
                Console.WriteLine($"Keep-alive timeout ({keepAliveTimeoutSeconds}s) reached for {socket.RemoteEndPoint}");
                break;
            }
            catch (SocketException)
            {
                // Client disconnected or network error
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
        catch { /* Ignore close errors */ }
        
        // Remove client from dictionary when connection is finally closed
        if (socket.RemoteEndPoint != null && clients.ContainsKey(socket.RemoteEndPoint))
        {
            clients.Remove(socket.RemoteEndPoint);
        }
    }
}

async Task<string> ReadData(Socket socket, Encoding encoding, CancellationToken cancellationToken = default)
{
    var buffer = new byte[4096];
    var bytesReceived = await socket.ReceiveAsync(buffer, cancellationToken);
    var message = encoding.GetString(buffer, 0, bytesReceived);
    return message.TrimEnd('\0', ' ', '\r', '\n');
}

async Task<bool> TryHandleHttpRequest(string message, Socket socket, HashSet<DirectoryInfo> allowedResourceDirectories, CancellationToken cancellationToken = default)
{
    if (message.Length == 0)
    {
        socket.Close();
        return false;
    }

    var lines = message.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    if (lines.Length <= 0)
    {
        socket.Close();
        return false;
    }

    var httpRequestLine = lines[0];
    var splitHttpRequestLine = httpRequestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

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

    // Check for Connection: keep-alive header
    var keepAlive = CheckKeepAlive(lines);

    switch (method.ToUpperInvariant())
    {
        case Constants.HttpMethodGet:
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

        // Try to find the resource in the allowed directories
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
            // Generate directory listing
            var directoryListing = GenerateDirectoryListing(matchingDirectory, pathOnly, allowedResourceDirectories);
            await SendResponse(socket, Constants.HttpResponseOk, "text/html", directoryListing, keepAlive, cancellationToken);
        }
        else if (matchingFile != null)
        {
            // Serve the file - use streaming for better memory efficiency
            await SendFileResponse(socket, matchingFile, keepAlive, cancellationToken);
        }
        else
        {
            // Try to find index.html in directories if path ends with /
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
                await SendResponse(socket, Constants.HttpResponseNotFound, "text/plain", "404 Not Found", keepAlive, cancellationToken);
            }
        }
    }
    catch (SecurityException)
    {
        await SendResponse(socket, Constants.HttpResponseForbidden, "text/plain", "403 Forbidden", keepAlive, cancellationToken);
    }
    finally
    {
        // Connection management is now handled by the caller
        // Don't close socket here as we may want to keep it alive
    }
}

bool CheckKeepAlive(string[] headers)
{
    foreach (var header in headers)
    {
        if (header.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
        {
            return header.Contains("keep-alive", StringComparison.OrdinalIgnoreCase);
        }
    }
    return false; // Default to close connection if no keep-alive header
}

async Task SendResponse(Socket socket, string statusCode, string contentType, string content, bool keepAlive, CancellationToken cancellationToken = default)
{
    var contentBytes = Encoding.UTF8.GetBytes(content);
    var headers = $"HTTP/1.1 {statusCode}\r\n" +
                 $"Content-Type: {contentType}\r\n" +
                 $"Content-Length: {contentBytes.Length}\r\n" +
                 $"Connection: {(keepAlive ? "keep-alive" : "close")}\r\n";
    
    if (keepAlive)
    {
        headers += $"Keep-Alive: timeout=60, max=100\r\n";
    }
    
    headers += "\r\n";
    
    var headerBytes = Encoding.UTF8.GetBytes(headers);
    
    // Log only headers, not content
    Console.WriteLine($"Server -> Client {socket.RemoteEndPoint}: {headers.TrimEnd()}");
    
    // Send headers
    await socket.SendAsync(headerBytes, cancellationToken);
    
    // Send content
    await socket.SendAsync(contentBytes, cancellationToken);
}

async Task SendFileResponse(Socket socket, FileInfo file, bool keepAlive, CancellationToken cancellationToken = default)
{
    var contentType = GetContentType(file.Extension);
    var headers = $"HTTP/1.1 {Constants.HttpResponseOk}\r\n" +
                 $"Content-Type: {contentType}\r\n" +
                 $"Content-Length: {file.Length}\r\n" +
                 $"Connection: {(keepAlive ? "keep-alive" : "close")}\r\n";
    
    if (keepAlive)
    {
        headers += $"Keep-Alive: timeout=60, max=100\r\n";
    }
    
    headers += "\r\n";
    
    var headerBytes = Encoding.UTF8.GetBytes(headers);
    
    // Log only headers, not content
    Console.WriteLine($"Server -> Client {socket.RemoteEndPoint}: {headers.TrimEnd()}");
    
    // Send headers
    await socket.SendAsync(headerBytes, cancellationToken);
    
    // Stream file content in chunks to avoid loading entire file into memory
    using var fileStream = file.OpenRead();
    var buffer = new byte[8192];
    int bytesRead;
    while ((bytesRead = await fileStream.ReadAsync(buffer, cancellationToken)) > 0)
    {
        await socket.SendAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
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
    
    // Add parent directory link if not at root
    if (requestPath != "/" && CanNavigateToParent(directory, allowedResourceDirectories))
    {
        var parentPath = requestPath.TrimEnd('/');
        var lastSlash = parentPath.LastIndexOf('/');
        var parentRequestPath = lastSlash <= 0 ? "/" : parentPath[..lastSlash] + "/";
        html.AppendLine($"<a href=\"{parentRequestPath}\">../</a>");
    }
    
    try
    {
        // List directories first
        var directories = directory.GetDirectories()
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);
        
        foreach (var dir in directories)
        {
            var dirPath = requestPath.EndsWith("/") ? requestPath + dir.Name + "/" : requestPath + "/" + dir.Name + "/";
            html.AppendLine($"<a href=\"{dirPath}\">{dir.Name}/</a>");
        }
        
        // Then list files
        var files = directory.GetFiles()
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
            
        foreach (var file in files)
        {
            var filePath = requestPath.EndsWith("/") ? requestPath + file.Name : requestPath + "/" + file.Name;
            var formattedSize = FormatFileSize(file.Length);
            var lastModified = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            html.AppendLine($"<a href=\"{filePath}\">{file.Name}</a>    {lastModified}    {formattedSize}");
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
    
    // Check if parent is within allowed directories
    return allowedResourceDirectories.Any(allowedDirectory => 
        parent.FullName.Equals(allowedDirectory.FullName) || 
        parent.FullName.StartsWith(allowedDirectory.FullName + Path.DirectorySeparatorChar));
}

string FormatFileSize(long bytes)
{
    string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
    int counter = 0;
    decimal number = bytes;
    while (Math.Round(number / 1024) >= 1)
    {
        number /= 1024;
        counter++;
    }
    return $"{number:n1}{suffixes[counter]}";
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
    public const string HttpMethodGet = "GET";

    public const string HttpResponseOk = "200 OK";
    public const string HttpResponseNotFound = "404 Not Found";
    public const string HttpResponseForbidden = "403 Forbidden";
}
